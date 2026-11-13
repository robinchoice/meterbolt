#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using StoreData = BTCPayServer.Data.StoreData;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Meterbolt.Data;
using BTCPayServer.Plugins.Meterbolt.L402;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Meterbolt.Services;

public class MeterboltSettings
{
    public const string Name = "Meterbolt";
    public const long DefaultMaxPriceSats = 10_000;
    public long MaxPriceSats { get; set; } = DefaultMaxPriceSats;
    public bool Discovery { get; set; }
}

public record Challenge(
    string Macaroon,
    string Invoice,
    string PaymentHash,
    string InvoiceId,
    long AmountSats,
    DateTimeOffset InvoiceExpiresAt,
    DateTimeOffset ExpiresAt)
{
    public string WwwAuthenticate => $"L402 macaroon=\"{Macaroon}\", invoice=\"{Invoice}\"";
}

public record VerifyResult(bool Valid, string? Reason = null, IssuedToken? Token = null, PaidEndpoint? Endpoint = null)
{
    public static VerifyResult Fail(string reason) => new(false, reason);
}

public record EndpointStats(long RevenueSats, long Calls);

public class ChallengeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class MeterboltService(
    IDbContextFactory<MeterboltDbContext> dbFactory,
    StoreRepository storeRepository,
    InvoiceRepository invoiceRepository,
    UIInvoiceController invoiceController,
    PaymentMethodHandlerDictionary handlers)
{
    public const string InvoiceTag = "meterbolt";
    public static readonly TimeSpan InvoiceExpiration = TimeSpan.FromMinutes(10);
    static readonly PaymentMethodId LightningBtc = PaymentTypes.LN.GetPaymentMethodId("BTC");

    public async Task<MeterboltSettings> GetSettings(string storeId)
        => await storeRepository.GetSettingAsync<MeterboltSettings>(storeId, MeterboltSettings.Name) ?? new MeterboltSettings();

    public Task SaveSettings(string storeId, MeterboltSettings settings)
        => storeRepository.UpdateSetting(storeId, MeterboltSettings.Name, settings);

    public async Task<RootKey> GetActiveRootKey(string storeId)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var key = await ctx.RootKeys.Where(k => k.StoreId == storeId && k.Active)
            .OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync();
        return key ?? await RotateRootKey(storeId);
    }

    /// <summary>Creates a new active root key. Older keys stay valid for the tokens they signed.</summary>
    public async Task<RootKey> RotateRootKey(string storeId)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new RootKey
        {
            Id = $"rk_{now:yyyyMMdd}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}",
            StoreId = storeId,
            Secret = RandomNumberGenerator.GetBytes(32),
            Active = true,
            CreatedAt = now
        };
        await ctx.RootKeys.Where(k => k.StoreId == storeId && k.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Active, false));
        ctx.RootKeys.Add(key);
        await ctx.SaveChangesAsync();
        return key;
    }

    public async Task<Challenge> CreateChallenge(StoreData store, PaidEndpoint endpoint, string serverUrl, CancellationToken cancellationToken)
    {
        var settings = await GetSettings(store.Id);
        if (!endpoint.Enabled)
            throw new ChallengeException("endpoint-disabled", "This endpoint is disabled.");
        if (endpoint.PriceSats > settings.MaxPriceSats)
            throw new ChallengeException("price-above-maximum", $"The endpoint price exceeds the store maximum of {settings.MaxPriceSats} sats.");
        if (!store.IsLightningEnabled("BTC"))
            throw new ChallengeException("lightning-unavailable", "Lightning is not set up for this store.");

        var request = new CreateInvoiceRequest
        {
            Amount = endpoint.PriceSats,
            Currency = "SATS",
            Metadata = new JObject { ["orderId"] = $"l402:{endpoint.Slug}", ["itemDesc"] = endpoint.Name },
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = [LightningBtc.ToString()],
                Expiration = InvoiceExpiration,
                LazyPaymentMethods = false
            },
            Receipt = new InvoiceDataBase.ReceiptOptions { Enabled = false }
        };
        InvoiceEntity invoice;
        try
        {
            invoice = await invoiceController.CreateInvoiceCoreRaw(request, store, serverUrl, [InvoiceTag], cancellationToken);
        }
        catch (BitpayHttpException e)
        {
            throw new ChallengeException("lightning-unavailable", e.Message);
        }
        var prompt = invoice.GetPaymentPrompt(LightningBtc);
        if (prompt?.Destination is null || !prompt.Activated)
            throw new ChallengeException("lightning-unavailable", "BTCPay could not create a Lightning invoice.");
        var details = handlers.GetLightningHandler("BTC").ParsePaymentPromptDetails(prompt.Details);
        var paymentHash = details.PaymentHash.ToString();
        // A store rate spread also applies to SATS invoices, so record what the BOLT11 invoice really asks for.
        var chargedSats = (long)Math.Ceiling(prompt.Calculate().Due * 100_000_000m);
        if (chargedSats > settings.MaxPriceSats)
            throw new ChallengeException("price-above-maximum", $"With the store's rate spread the invoice would exceed the store maximum of {settings.MaxPriceSats} sats.");

        var rootKey = await GetActiveRootKey(store.Id);
        var tokenId = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(endpoint.TokenTtlSeconds);
        var macaroon = Macaroon.Create(rootKey.Secret, new L402Identifier(Convert.FromHexString(paymentHash), tokenId).Encode())
            .AddFirstPartyCaveat(L402Caveats.ServicesCaveat(endpoint.Slug))
            .AddFirstPartyCaveat(L402Caveats.ValidUntilCaveat(endpoint.Slug, expiresAt));

        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);
        ctx.Tokens.Add(new IssuedToken
        {
            PaymentHash = paymentHash,
            TokenId = tokenId,
            StoreId = store.Id,
            EndpointId = endpoint.Id,
            InvoiceId = invoice.Id,
            RootKeyId = rootKey.Id,
            PriceSats = chargedSats,
            SingleUse = endpoint.SingleUse,
            Status = TokenStatus.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });
        await ctx.SaveChangesAsync(cancellationToken);
        return new Challenge(macaroon.ToBase64(), prompt.Destination, paymentHash, invoice.Id, chargedSats, invoice.ExpirationTime, expiresAt);
    }

    public async Task<VerifyResult> Verify(string storeId, string? authorization, string? expectedSlug, CancellationToken cancellationToken)
    {
        if (!L402Credentials.TryParse(authorization, out var credentials, out var parseError) || credentials is null)
            return VerifyResult.Fail(parseError);

        var paymentHash = Convert.ToHexStringLower(credentials.Identifier.PaymentHash);
        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);
        var token = await ctx.Tokens.FirstOrDefaultAsync(t => t.PaymentHash == paymentHash && t.StoreId == storeId, cancellationToken);
        if (token is null || !CryptographicOperations.FixedTimeEquals(token.TokenId, credentials.Identifier.TokenId))
            return VerifyResult.Fail("unknown-token");
        var rootKey = await ctx.RootKeys.FirstOrDefaultAsync(k => k.Id == token.RootKeyId && k.StoreId == storeId, cancellationToken);
        if (rootKey is null || !credentials.Macaroon.HasValidSignature(rootKey.Secret))
            return VerifyResult.Fail("invalid-signature");
        var endpoint = await ctx.Endpoints.FirstOrDefaultAsync(e => e.Id == token.EndpointId, cancellationToken);
        if (endpoint is null)
            return VerifyResult.Fail("unknown-token");
        if (expectedSlug is not null && expectedSlug != endpoint.Slug)
            return VerifyResult.Fail("wrong-endpoint");

        var now = DateTimeOffset.UtcNow;
        if (!L402Caveats.Satisfied(credentials.Macaroon.Caveats, endpoint.Slug, now, out var caveatError))
            return VerifyResult.Fail(caveatError);
        if (!L402Caveats.PreimageMatches(credentials.Preimage, credentials.Identifier.PaymentHash))
            return VerifyResult.Fail("invalid-preimage");
        if (token.RevokedAt is not null)
            return VerifyResult.Fail("revoked");
        if (now >= token.ExpiresAt)
            return VerifyResult.Fail("expired");
        if (!endpoint.Enabled)
            return VerifyResult.Fail("endpoint-disabled");

        if (token.Status != TokenStatus.Paid)
        {
            // The invoice listener normally marks the token; ask BTCPay directly in case it has not run yet.
            var invoice = await invoiceRepository.GetInvoice(token.InvoiceId);
            if (invoice?.Status != InvoiceStatus.Settled)
                return VerifyResult.Fail("not-paid");
            await MarkInvoice(dbFactory, token.InvoiceId, InvoiceStatus.Settled);
            token.Status = TokenStatus.Paid;
        }

        var consumed = await ctx.Tokens
            .Where(t => t.PaymentHash == paymentHash && (!t.SingleUse || t.Uses == 0))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Uses, t => t.Uses + 1), cancellationToken);
        if (consumed == 0)
            return VerifyResult.Fail("already-used");
        token.Uses++;

        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO meterbolt.daily_calls (endpoint_id, day, calls) VALUES ({endpoint.Id}, {day}, 1) ON CONFLICT (endpoint_id, day) DO UPDATE SET calls = daily_calls.calls + 1",
            cancellationToken);
        return new VerifyResult(true, null, token, endpoint);
    }

    public static async Task MarkInvoice(IDbContextFactory<MeterboltDbContext> dbFactory, string invoiceId, InvoiceStatus status)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        if (status == InvoiceStatus.Settled)
        {
            var now = DateTimeOffset.UtcNow;
            await ctx.Tokens.Where(t => t.InvoiceId == invoiceId && t.Status != TokenStatus.Paid)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TokenStatus.Paid).SetProperty(t => t.PaidAt, now));
        }
        else if (status is InvoiceStatus.Expired or InvoiceStatus.Invalid)
        {
            await ctx.Tokens.Where(t => t.InvoiceId == invoiceId && t.Status == TokenStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TokenStatus.Expired));
        }
    }

    public async Task<bool> Revoke(string storeId, string paymentHash)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        return await ctx.Tokens.Where(t => t.StoreId == storeId && t.PaymentHash == paymentHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now)) > 0;
    }

    public async Task<PaidEndpoint?> GetEndpointBySlug(string storeId, string slug)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Endpoints.FirstOrDefaultAsync(e => e.StoreId == storeId && e.Slug == slug);
    }

    public async Task<PaidEndpoint?> GetEndpoint(string storeId, string id)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Endpoints.FirstOrDefaultAsync(e => e.StoreId == storeId && e.Id == id);
    }

    public async Task<List<PaidEndpoint>> GetEndpoints(string storeId)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Endpoints.Where(e => e.StoreId == storeId).OrderBy(e => e.CreatedAt).ToListAsync();
    }

    public async Task<bool> SlugExists(string storeId, string slug, string? exceptId)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Endpoints.AnyAsync(e => e.StoreId == storeId && e.Slug == slug && e.Id != exceptId);
    }

    public async Task SaveEndpoint(PaidEndpoint endpoint)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        if (await ctx.Endpoints.AnyAsync(e => e.Id == endpoint.Id))
            ctx.Endpoints.Update(endpoint);
        else
            ctx.Endpoints.Add(endpoint);
        await ctx.SaveChangesAsync();
    }

    /// <summary>Revenue of tokens paid and calls verified in the last seven days, per endpoint.</summary>
    public async Task<Dictionary<string, EndpointStats>> GetStats(string storeId)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var sinceDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6));
        var revenue = await ctx.Tokens.Where(t => t.StoreId == storeId && t.PaidAt >= since)
            .GroupBy(t => t.EndpointId)
            .Select(g => new { g.Key, Sum = g.Sum(t => t.PriceSats) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        var endpointIds = ctx.Endpoints.Where(e => e.StoreId == storeId).Select(e => e.Id);
        var calls = await ctx.DailyCalls.Where(d => endpointIds.Contains(d.EndpointId) && d.Day >= sinceDay)
            .GroupBy(d => d.EndpointId)
            .Select(g => new { g.Key, Sum = g.Sum(d => d.Calls) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        return revenue.Keys.Union(calls.Keys).ToDictionary(id => id,
            id => new EndpointStats(revenue.GetValueOrDefault(id), calls.GetValueOrDefault(id)));
    }

    public async Task<List<IssuedToken>> GetTokens(string endpointId, bool paidOnly, int take)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var query = ctx.Tokens.Where(t => t.EndpointId == endpointId);
        if (paidOnly)
        {
            var now = DateTimeOffset.UtcNow;
            query = query.Where(t => t.Status == TokenStatus.Paid && t.RevokedAt == null && t.ExpiresAt > now && (!t.SingleUse || t.Uses == 0));
        }
        return await query.OrderByDescending(t => t.CreatedAt).Take(take).ToListAsync();
    }

    public async Task<List<(string StoreName, PaidEndpoint Endpoint)>> GetDiscoverableEndpoints()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var endpoints = await ctx.Endpoints.Where(e => e.Enabled && e.PublicUrl != null).ToListAsync();
        var result = new List<(string, PaidEndpoint)>();
        foreach (var group in endpoints.GroupBy(e => e.StoreId))
        {
            if (!(await GetSettings(group.Key)).Discovery)
                continue;
            var store = await storeRepository.FindStore(group.Key);
            if (store is null)
                continue;
            result.AddRange(group.Select(e => (store.StoreName, e)));
        }
        return result;
    }
}
