#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace BTCPayServer.Plugins.Meterbolt.L402;

/// <summary>
/// L402 macaroon identifier as used by Aperture: version (uint16 BE, 0) | payment hash (32) | token id (32).
/// </summary>
public sealed record L402Identifier(byte[] PaymentHash, byte[] TokenId)
{
    public const int Length = 2 + 32 + 32;

    public byte[] Encode()
    {
        var id = new byte[Length];
        BinaryPrimitives.WriteUInt16BigEndian(id, 0);
        PaymentHash.CopyTo(id, 2);
        TokenId.CopyTo(id, 34);
        return id;
    }

    public static bool TryDecode(byte[] data, out L402Identifier? identifier)
    {
        identifier = null;
        if (data.Length != Length || BinaryPrimitives.ReadUInt16BigEndian(data) != 0)
            return false;
        identifier = new L402Identifier(data[2..34], data[34..66]);
        return true;
    }
}

/// <summary>Parsed value of an <c>Authorization: L402 &lt;macaroon&gt;:&lt;preimage&gt;</c> header.</summary>
public sealed record L402Credentials(Macaroon Macaroon, L402Identifier Identifier, byte[] Preimage)
{
    public static bool TryParse(string? header, out L402Credentials? credentials, out string error)
    {
        credentials = null;
        error = "malformed";
        if (string.IsNullOrWhiteSpace(header))
            return false;
        var value = header.Trim();
        var space = value.IndexOf(' ');
        if (space < 0)
            return false;
        var scheme = value[..space];
        if (!scheme.Equals("L402", StringComparison.OrdinalIgnoreCase))
            return false;
        value = value[(space + 1)..].Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0)
            return false;
        var macaroonPart = value[..colon];
        if (macaroonPart.Contains(','))
        {
            error = "multiple-macaroons-unsupported";
            return false;
        }
        if (!Macaroon.TryParseBase64(macaroonPart, out var macaroon) || macaroon is null)
            return false;
        if (!L402Identifier.TryDecode(macaroon.Identifier, out var identifier) || identifier is null)
            return false;
        byte[] preimage;
        try
        {
            preimage = Convert.FromHexString(value[(colon + 1)..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (preimage.Length != 32)
            return false;
        credentials = new L402Credentials(macaroon, identifier, preimage);
        error = "";
        return true;
    }
}

public static class L402Caveats
{
    public const string Services = "services";
    public const string ValidUntilSuffix = "_valid_until";

    public static string ServicesCaveat(string slug) => $"{Services}={slug}:0";
    public static string ValidUntilCaveat(string slug, DateTimeOffset until)
        => $"{slug}{ValidUntilSuffix}={until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Checks every caveat against the endpoint and time. Unknown conditions fail closed,
    /// repeated conditions (client-side attenuation) must all hold.
    /// </summary>
    public static bool Satisfied(IEnumerable<string> caveats, string slug, DateTimeOffset now, out string error)
    {
        error = "";
        foreach (var caveat in caveats)
        {
            var eq = caveat.IndexOf('=');
            if (eq <= 0)
            {
                error = "unknown-caveat";
                return false;
            }
            var key = caveat[..eq].Trim();
            var val = caveat[(eq + 1)..].Trim();
            if (key == Services)
            {
                var allowed = false;
                foreach (var service in val.Split(','))
                {
                    var name = service.Split(':')[0].Trim();
                    if (name == slug)
                        allowed = true;
                }
                if (!allowed)
                {
                    error = "wrong-endpoint";
                    return false;
                }
            }
            else if (key.EndsWith(ValidUntilSuffix, StringComparison.Ordinal))
            {
                // Expiry caveats for other services do not restrict this endpoint.
                if (key != slug + ValidUntilSuffix)
                    continue;
                if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) ||
                    now.ToUnixTimeSeconds() >= unix)
                {
                    error = "expired";
                    return false;
                }
            }
            else
            {
                error = "unknown-caveat";
                return false;
            }
        }
        return true;
    }

    public static bool PreimageMatches(byte[] preimage, byte[] paymentHash)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(preimage), paymentHash);
}
