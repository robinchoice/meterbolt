using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Meterbolt.Data;
using BTCPayServer.Plugins.Meterbolt.L402;
using BTCPayServer.Plugins.Meterbolt.Services;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Meterbolt.Tests;

/// <summary>
/// Regtest only: needs the services from submodules/btcpayserver/BTCPayServer.Tests/docker-compose.yml.
/// </summary>
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class L402IntegrationTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    [Fact(Timeout = 5 * 60 * 1000)]
    [Trait("Integration", "Integration")]
    [Trait("Lightning", "Lightning")]
    public async Task ChallengePayVerify()
    {
        using var tester = CreateServerTester();
        tester.ActivateLightning();
        await tester.StartAsync();
        await tester.EnsureChannelsSetup();
        var user = tester.NewAccount();
        await user.GrantAccessAsync(true);
        await user.RegisterLightningNodeAsync("BTC", LightningTestImplementation.CoreLightning);
        var storeId = user.StoreId;

        var lookup = await SaveEndpoint(tester, storeId, "song-lookup", 10, singleUse: false);
        var once = await SaveEndpoint(tester, storeId, "one-shot", 21, singleUse: true);

        var appKey = await CreateKey(user, $"{MeterboltPolicies.CanIssue}:{storeId}", $"{MeterboltPolicies.CanVerify}:{storeId}");
        var verifyOnlyKey = await CreateKey(user, $"{MeterboltPolicies.CanVerify}:{storeId}");
        var api = tester.PayTester.HttpClient;

        // Only the issue permission may create challenges, and only for its own store.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(api, verifyOnlyKey, $"api/v1/stores/{storeId}/l402/endpoints/song-lookup/challenges", null)).Status);
        var otherUser = tester.NewAccount();
        await otherUser.GrantAccessAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(api, appKey, $"api/v1/stores/{otherUser.StoreId}/l402/endpoints/song-lookup/challenges", null)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(api, appKey, $"api/v1/stores/{storeId}/l402/endpoints/nope/challenges", null)).Status);

        // Challenge: a Lightning-only BTCPay invoice at the server-side price, plus a macaroon bound to its hash.
        var challenge = await Challenge(api, appKey, storeId, "song-lookup");
        var bolt11 = BOLT11PaymentRequest.Parse(challenge["invoice"]!.Value<string>()!, Network.RegTest);
        Assert.Equal(LightMoney.Satoshis(10), bolt11.MinimumAmount);
        Assert.Equal(bolt11.PaymentHash!.ToString(), challenge["paymentHash"]!.Value<string>());
        Assert.StartsWith("L402 macaroon=\"", challenge["wwwAuthenticate"]!.Value<string>());
        Assert.True(Macaroon.TryParseBase64(challenge["macaroon"]!.Value<string>()!, out var macaroon));
        Assert.Contains("services=song-lookup:0", macaroon!.Caveats);

        var adminClient = await user.CreateClient(Policies.Unrestricted);
        var invoice = await adminClient.GetInvoice(challenge["invoiceId"]!.Value<string>()!);
        Assert.Equal("SATS", invoice.Currency);
        Assert.Equal(10m, invoice.Amount);
        Assert.Equal("l402:song-lookup", invoice.Metadata["orderId"]!.Value<string>());
        var methods = await adminClient.GetInvoicePaymentMethods(invoice.Id);
        Assert.Equal("BTC-LN", Assert.Single(methods).PaymentMethodId);

        // Before payment the preimage is unknown; a random one must fail.
        var fakePreimage = Convert.ToHexStringLower(RandomUtils.GetBytes(32));
        Assert.Equal("invalid-preimage", (await Verify(api, appKey, storeId, Header(challenge, fakePreimage)))["reason"]!.Value<string>());

        // Pay from the customer node and wait until BTCPay settles the invoice.
        var pay = await tester.CustomerLightningD.Pay(bolt11.ToString());
        Assert.Equal(PayResult.Ok, pay.Result);
        var preimage = pay.Details.Preimage.ToString();
        await TestUtils.EventuallyAsync(async () =>
            Assert.Equal(InvoiceStatus.Settled, (await adminClient.GetInvoice(invoice.Id)).Status));

        var header = Header(challenge, preimage);
        var ok = await Verify(api, appKey, storeId, header, "song-lookup");
        Assert.True(ok["valid"]!.Value<bool>(), ok.ToString());
        Assert.Equal("song-lookup", ok["endpoint"]!.Value<string>());
        Assert.Equal(1, ok["uses"]!.Value<int>());
        // Multi-use tokens verify again until they expire.
        Assert.Equal(2, (await Verify(api, appKey, storeId, header))["uses"]!.Value<int>());
        Assert.Equal("wrong-endpoint", (await Verify(api, appKey, storeId, header, "one-shot"))["reason"]!.Value<string>());
        Assert.Equal("malformed", (await Verify(api, appKey, storeId, "Bearer abc"))["reason"]!.Value<string>());

        // A caveat added by an attacker breaks the signature; one added by the client still verifies.
        Assert.True(Macaroon.TryParseBase64(challenge["macaroon"]!.Value<string>()!, out var attenuated));
        attenuated!.AddFirstPartyCaveat(L402Caveats.ValidUntilCaveat("song-lookup", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.True((await Verify(api, appKey, storeId, $"L402 {attenuated.ToBase64()}:{preimage}"))["valid"]!.Value<bool>());
        var forged = attenuated.Serialize();
        forged[^1] ^= 1;
        Assert.Equal("invalid-signature", (await Verify(api, appKey, storeId, $"L402 {Convert.ToBase64String(forged)}:{preimage}"))["reason"]!.Value<string>());

        // Root key rotation: the old token keeps working, new tokens use the new key.
        using (var scope = tester.PayTester.ServiceProvider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MeterboltService>().RotateRootKey(storeId);
        Assert.True((await Verify(api, appKey, storeId, header))["valid"]!.Value<bool>());

        // The token is bound to this store.
        var otherKey = await CreateKey(otherUser, $"{MeterboltPolicies.CanVerify}:{otherUser.StoreId}");
        Assert.Equal("unknown-token", (await Verify(api, otherKey, otherUser.StoreId, header))["reason"]!.Value<string>());

        // Revocation.
        using (var scope = tester.PayTester.ServiceProvider.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MeterboltService>().Revoke(storeId, challenge["paymentHash"]!.Value<string>()!));
        Assert.Equal("revoked", (await Verify(api, appKey, storeId, header))["reason"]!.Value<string>());

        // Single-use tokens verify exactly once.
        var oneShot = await Challenge(api, appKey, storeId, "one-shot");
        var pay2 = await tester.CustomerLightningD.Pay(oneShot["invoice"]!.Value<string>()!);
        Assert.Equal(PayResult.Ok, pay2.Result);
        var oneShotHeader = Header(oneShot, pay2.Details.Preimage.ToString());
        await TestUtils.EventuallyAsync(async () =>
            Assert.True((await Verify(api, appKey, storeId, oneShotHeader))["valid"]!.Value<bool>()));
        Assert.Equal("already-used", (await Verify(api, appKey, storeId, oneShotHeader))["reason"]!.Value<string>());

        // Stats count tokens and verified calls, nothing about buyers.
        using (var scope = tester.PayTester.ServiceProvider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MeterboltService>();
            var stats = await service.GetStats(storeId);
            Assert.Equal(10, stats[lookup.Id].RevenueSats);
            Assert.Equal(4, stats[lookup.Id].Calls);
            Assert.Equal(21, stats[once.Id].RevenueSats);
            Assert.Equal(1, stats[once.Id].Calls);

            // Max price guard.
            await service.SaveSettings(storeId, new MeterboltSettings { MaxPriceSats = 15 });
        }
        var tooExpensive = await Post(api, appKey, $"api/v1/stores/{storeId}/l402/endpoints/one-shot/challenges", null);
        Assert.Equal(HttpStatusCode.BadRequest, tooExpensive.Status);
        Assert.Contains("price-above-maximum", tooExpensive.Body);

        // Discovery is off by default and lists endpoints with a public URL once switched on.
        Assert.Empty(JObject.Parse(await api.GetStringAsync(".well-known/l402.json"))["endpoints"]!);
        using (var scope = tester.PayTester.ServiceProvider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MeterboltService>().SaveSettings(storeId, new MeterboltSettings { Discovery = true });
        var discovery = JObject.Parse(await api.GetStringAsync(".well-known/l402.json"));
        Assert.Equal(2, discovery["endpoints"]!.Count());
        Assert.Contains(discovery["endpoints"]!, e => e["url"]!.Value<string>() == "https://api.example.org/song-lookup");
    }

    static async Task<PaidEndpoint> SaveEndpoint(ServerTester tester, string storeId, string slug, long price, bool singleUse)
    {
        var endpoint = new PaidEndpoint
        {
            Id = $"{slug}-{Guid.NewGuid():N}",
            StoreId = storeId,
            Slug = slug,
            Name = slug,
            PriceSats = price,
            TokenTtlSeconds = 3600,
            SingleUse = singleUse,
            PublicUrl = $"https://api.example.org/{slug}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        using var scope = tester.PayTester.ServiceProvider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MeterboltService>().SaveEndpoint(endpoint);
        return endpoint;
    }

    static async Task<string> CreateKey(TestAccount user, params string[] permissions)
    {
        var client = await user.CreateClient();
        var key = await client.CreateAPIKey(new CreateApiKeyRequest { Label = "Meterbolt test", Permissions = permissions.Select(Permission.Parse).ToArray() });
        return key.ApiKey;
    }

    static string Header(JObject challenge, string preimageHex) => $"L402 {challenge["macaroon"]!.Value<string>()}:{preimageHex}";

    static async Task<JObject> Challenge(HttpClient api, string key, string storeId, string slug)
    {
        var r = await Post(api, key, $"api/v1/stores/{storeId}/l402/endpoints/{slug}/challenges", null);
        Assert.True(r.Status == HttpStatusCode.Created, r.Body);
        return JObject.Parse(r.Body);
    }

    static async Task<JObject> Verify(HttpClient api, string key, string storeId, string authorization, string? endpoint = null)
    {
        var r = await Post(api, key, $"api/v1/stores/{storeId}/l402/verify", new JObject { ["authorization"] = authorization, ["endpoint"] = endpoint });
        Assert.True(r.Status == HttpStatusCode.OK, r.Body);
        return JObject.Parse(r.Body);
    }

    static async Task<(HttpStatusCode Status, string Body)> Post(HttpClient api, string key, string path, JObject? body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", key);
        req.Content = new StringContent(body?.ToString() ?? "{}", Encoding.UTF8, "application/json");
        using var res = await api.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}
