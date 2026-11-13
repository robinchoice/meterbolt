using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.Meterbolt.L402;

namespace BTCPayServer.Plugins.Meterbolt.Tests;

[Trait("Fast", "Fast")]
public class MacaroonTests
{
    static readonly byte[] RootKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    static readonly byte[] Hash = Enumerable.Repeat((byte)0xaa, 32).ToArray();
    static readonly byte[] TokenId = Enumerable.Repeat((byte)0xbb, 32).ToArray();
    static readonly DateTimeOffset Until = DateTimeOffset.FromUnixTimeSeconds(1763129520);

    // Generated with pymacaroons 0.13 (V2 binary, base64url), see AGENTS.md.
    const string PyMacaroonsVector = "AgEAAkIAAKqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqu7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7u7sAAhZzZXJ2aWNlcz1zb25nLWxvb2t1cDowAAIic29uZy1sb29rdXBfdmFsaWRfdW50aWw9MTc2MzEyOTUyMAAABiDAb34VHDQYykvdV9wfK5v72n5FGoA58do6dTEFhn3AsA";
    const string PyMacaroonsSignature = "c06f7e151c3418ca4bdd57dc1f2b9bfbda7e451a8039f1da3a753105867dc0b0";

    static Macaroon Sample() => Macaroon.Create(RootKey, new L402Identifier(Hash, TokenId).Encode())
        .AddFirstPartyCaveat(L402Caveats.ServicesCaveat("song-lookup"))
        .AddFirstPartyCaveat(L402Caveats.ValidUntilCaveat("song-lookup", Until));

    [Fact]
    public void MatchesReferenceImplementation()
    {
        Assert.Equal(PyMacaroonsSignature, Convert.ToHexStringLower(Sample().Signature));

        Assert.True(Macaroon.TryParseBase64(PyMacaroonsVector, out var parsed));
        Assert.NotNull(parsed);
        Assert.True(parsed.HasValidSignature(RootKey));
        Assert.Equal(new[] { "services=song-lookup:0", "song-lookup_valid_until=1763129520" }, parsed.Caveats);
        Assert.True(L402Identifier.TryDecode(parsed.Identifier, out var id));
        Assert.Equal(Hash, id!.PaymentHash);
        Assert.Equal(TokenId, id.TokenId);
    }

    [Fact]
    public void RoundTripsThroughBase64()
    {
        var m = Sample();
        Assert.True(Macaroon.TryParseBase64(m.ToBase64(), out var parsed));
        Assert.Equal(m.Serialize(), parsed!.Serialize());
        Assert.True(parsed.HasValidSignature(RootKey));
        Assert.False(parsed.HasValidSignature(RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void DetectsTampering()
    {
        var bytes = Sample().Serialize();
        var text = Encoding.ASCII.GetBytes("1763129520");
        var at = bytes.AsSpan().IndexOf(text);
        bytes[at] = (byte)'2'; // push the expiry into the future
        Assert.True(Macaroon.TryDeserialize(bytes, out var forged));
        Assert.False(forged!.HasValidSignature(RootKey));
    }

    [Fact]
    public void ClientAttenuationKeepsSignatureValid()
    {
        var m = Sample();
        Assert.True(Macaroon.TryParseBase64(m.ToBase64(), out var copy));
        copy!.AddFirstPartyCaveat(L402Caveats.ValidUntilCaveat("song-lookup", Until.AddMinutes(-5)));
        Assert.True(copy.HasValidSignature(RootKey));
        Assert.False(L402Caveats.Satisfied(copy.Caveats, "song-lookup", Until.AddMinutes(-1), out var error));
        Assert.Equal("expired", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA")]
    [InlineData("not base64!")]
    [InlineData("AgEAAkIA")]
    public void RejectsGarbage(string value)
    {
        Assert.False(Macaroon.TryParseBase64(value, out _));
    }

    [Fact]
    public void ChecksCaveats()
    {
        var now = Until.AddHours(-1);
        Assert.True(L402Caveats.Satisfied(Sample().Caveats, "song-lookup", now, out _));
        Assert.False(L402Caveats.Satisfied(Sample().Caveats, "other", now, out var e1));
        Assert.Equal("wrong-endpoint", e1);
        Assert.False(L402Caveats.Satisfied(Sample().Caveats, "song-lookup", Until, out var e2));
        Assert.Equal("expired", e2);
        Assert.False(L402Caveats.Satisfied(["song-lookup_capabilities=read"], "song-lookup", now, out var e3));
        Assert.Equal("unknown-caveat", e3);
        Assert.True(L402Caveats.Satisfied(["services=a:0,song-lookup:0", "other_valid_until=1"], "song-lookup", now, out _));
    }

    [Fact]
    public void ParsesAuthorizationHeader()
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        var m = Macaroon.Create(RootKey, new L402Identifier(hash, TokenId).Encode());
        foreach (var scheme in new[] { "L402", "l402" })
        {
            Assert.True(L402Credentials.TryParse($"{scheme} {m.ToBase64()}:{Convert.ToHexStringLower(preimage)}", out var c, out _));
            Assert.True(L402Caveats.PreimageMatches(c!.Preimage, c.Identifier.PaymentHash));
        }
        var urlSafe = m.ToBase64().TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(L402Credentials.TryParse($"L402 {urlSafe}:{Convert.ToHexString(preimage)}", out _, out _));

        Assert.False(L402Credentials.TryParse($"Bearer {m.ToBase64()}:{Convert.ToHexStringLower(preimage)}", out _, out _));
        Assert.False(L402Credentials.TryParse($"L402 {m.ToBase64()}", out _, out _));
        Assert.False(L402Credentials.TryParse($"L402 {m.ToBase64()}:abcd", out _, out _));
        Assert.False(L402Credentials.TryParse($"L402 {m.ToBase64()},{m.ToBase64()}:{Convert.ToHexStringLower(preimage)}", out _, out var error));
        Assert.Equal("multiple-macaroons-unsupported", error);

        Assert.True(L402Credentials.TryParse($"L402 {m.ToBase64()}:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}", out var wrong, out _));
        Assert.False(L402Caveats.PreimageMatches(wrong!.Preimage, wrong.Identifier.PaymentHash));
    }
}
