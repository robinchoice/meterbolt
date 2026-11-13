#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Meterbolt.Data;
using BTCPayServer.Plugins.Meterbolt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Meterbolt.Controllers;

public class IndexViewModel
{
    public string StoreId { get; set; } = null!;
    public List<PaidEndpoint> Endpoints { get; set; } = new();
    public Dictionary<string, EndpointStats> Stats { get; set; } = new();
    public bool LightningEnabled { get; set; }
    public string RootKeyId { get; set; } = null!;
    public DateTimeOffset RootKeyCreatedAt { get; set; }
    public MeterboltSettings Settings { get; set; } = null!;
    public long TotalRevenue => Stats.Values.Sum(s => s.RevenueSats);
    public long TotalCalls => Stats.Values.Sum(s => s.Calls);
}

public class EndpointViewModel
{
    public string? Id { get; set; }

    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [StringLength(40)]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use lowercase letters, digits and dashes.")]
    public string? Slug { get; set; }

    [Display(Name = "Price per token")]
    [Range(1, 21_000_000_00_000_000)]
    public long PriceSats { get; set; } = 10;

    [Display(Name = "Token valid for")]
    public string Lifetime { get; set; } = "1h";

    [Display(Name = "Public URL (for discovery)")]
    [StringLength(500)]
    public string? PublicUrl { get; set; }

    public long MaxPriceSats { get; set; }
}

public class DetailViewModel
{
    public string StoreId { get; set; } = null!;
    public PaidEndpoint Endpoint { get; set; } = null!;
    public EndpointStats Stats { get; set; } = new(0, 0);
    public List<IssuedToken> Payments { get; set; } = new();
    public List<IssuedToken> ActiveTokens { get; set; } = new();
    public string ServerUrl { get; set; } = null!;
}

public static class Lifetimes
{
    public record Option(string Key, string Label, int Seconds, bool SingleUse);

    public static readonly Option[] All =
    [
        new("call", "1 call", 24 * 3600, true),
        new("10m", "10 min", 600, false),
        new("1h", "1 h", 3600, false),
        new("24h", "24 h", 24 * 3600, false),
        new("30d", "30 days", 30 * 24 * 3600, false)
    ];

    public static Option? Find(string key) => All.FirstOrDefault(o => o.Key == key);

    public static string Describe(PaidEndpoint e)
        => All.FirstOrDefault(o => o.Seconds == e.TokenTtlSeconds && o.SingleUse == e.SingleUse)?.Label
           ?? (e.SingleUse ? "1 call" : $"{TimeSpan.FromSeconds(e.TokenTtlSeconds):g}");

    public static string KeyOf(PaidEndpoint e)
        => All.FirstOrDefault(o => o.Seconds == e.TokenTtlSeconds && o.SingleUse == e.SingleUse)?.Key ?? "1h";
}

[Area(Plugin.Area)]
[Route("stores/{storeId}/meterbolt")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[AutoValidateAntiforgeryToken]
public class UIMeterboltController(MeterboltService service) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var rootKey = await service.GetActiveRootKey(store.Id);
        return View(new IndexViewModel
        {
            StoreId = store.Id,
            Endpoints = await service.GetEndpoints(store.Id),
            Stats = await service.GetStats(store.Id),
            LightningEnabled = store.IsLightningEnabled("BTC"),
            RootKeyId = rootKey.Id,
            RootKeyCreatedAt = rootKey.CreatedAt,
            Settings = await service.GetSettings(store.Id)
        });
    }

    [HttpGet("endpoints/new")]
    public async Task<IActionResult> Create(string storeId)
        => View("Edit", new EndpointViewModel { MaxPriceSats = (await service.GetSettings(storeId)).MaxPriceSats });

    [HttpGet("endpoints/{id}/edit")]
    public async Task<IActionResult> Edit(string storeId, string id)
    {
        var e = await service.GetEndpoint(storeId, id);
        if (e is null)
            return NotFound();
        return View(new EndpointViewModel
        {
            Id = e.Id,
            Name = e.Name,
            Slug = e.Slug,
            PriceSats = e.PriceSats,
            Lifetime = Lifetimes.KeyOf(e),
            PublicUrl = e.PublicUrl,
            MaxPriceSats = (await service.GetSettings(storeId)).MaxPriceSats
        });
    }

    [HttpPost("endpoints/new")]
    [HttpPost("endpoints/{id}/edit")]
    public async Task<IActionResult> Save(string storeId, string? id, EndpointViewModel vm)
    {
        var store = HttpContext.GetStoreData();
        var settings = await service.GetSettings(store.Id);
        vm.MaxPriceSats = settings.MaxPriceSats;
        var existing = id is null ? null : await service.GetEndpoint(store.Id, id);
        if (id is not null && existing is null)
            return NotFound();

        // The slug is part of URLs and macaroon caveats, so it never changes after creation.
        vm.Slug = existing?.Slug ?? (string.IsNullOrWhiteSpace(vm.Slug) ? Slugify(vm.Name) : vm.Slug.Trim());
        ModelState.Remove(nameof(vm.Slug));
        if (string.IsNullOrEmpty(vm.Slug) || !Regex.IsMatch(vm.Slug, "^[a-z0-9]+(-[a-z0-9]+)*$") || vm.Slug.Length > 40)
            ModelState.AddModelError(nameof(vm.Slug), "Use lowercase letters, digits and dashes.");
        else if (await service.SlugExists(store.Id, vm.Slug, existing?.Id))
            ModelState.AddModelError(nameof(vm.Slug), "Another endpoint already uses this slug.");
        if (vm.PriceSats > settings.MaxPriceSats)
            ModelState.AddModelError(nameof(vm.PriceSats), $"The store maximum is {settings.MaxPriceSats:N0} sats per challenge.");
        var lifetime = Lifetimes.Find(vm.Lifetime);
        if (lifetime is null)
            ModelState.AddModelError(nameof(vm.Lifetime), "Choose a token lifetime.");
        vm.PublicUrl = string.IsNullOrWhiteSpace(vm.PublicUrl) ? null : vm.PublicUrl.Trim();
        if (!ModelState.IsValid)
        {
            vm.Id = id;
            return View("Edit", vm);
        }

        var endpoint = existing ?? new PaidEndpoint
        {
            Id = Encoders.Base58.EncodeData(RandomUtils.GetBytes(16)),
            StoreId = store.Id,
            Slug = vm.Slug,
            CreatedAt = DateTimeOffset.UtcNow
        };
        endpoint.Name = vm.Name.Trim();
        endpoint.PriceSats = vm.PriceSats;
        endpoint.TokenTtlSeconds = lifetime!.Seconds;
        endpoint.SingleUse = lifetime.SingleUse;
        endpoint.PublicUrl = vm.PublicUrl;
        await service.SaveEndpoint(endpoint);
        TempData[WellKnownTempData.SuccessMessage] = existing is null ? "Paid endpoint created." : "Paid endpoint updated.";
        return RedirectToAction(nameof(Detail), new { storeId = store.Id, id = endpoint.Id });
    }

    [HttpGet("endpoints/{id}")]
    public async Task<IActionResult> Detail(string storeId, string id)
    {
        var e = await service.GetEndpoint(storeId, id);
        if (e is null)
            return NotFound();
        var stats = await service.GetStats(storeId);
        return View(new DetailViewModel
        {
            StoreId = storeId,
            Endpoint = e,
            Stats = stats.GetValueOrDefault(e.Id) ?? new EndpointStats(0, 0),
            Payments = await service.GetTokens(e.Id, paidOnly: false, take: 50),
            ActiveTokens = await service.GetTokens(e.Id, paidOnly: true, take: 50),
            ServerUrl = Request.GetAbsoluteRoot().TrimEnd('/')
        });
    }

    [HttpPost("endpoints/{id}/toggle")]
    public async Task<IActionResult> Toggle(string storeId, string id)
    {
        var e = await service.GetEndpoint(storeId, id);
        if (e is null)
            return NotFound();
        e.Enabled = !e.Enabled;
        await service.SaveEndpoint(e);
        TempData[WellKnownTempData.SuccessMessage] = e.Enabled
            ? "Endpoint enabled."
            : "Endpoint disabled. New challenges are refused and existing tokens stop verifying.";
        return RedirectToAction(nameof(Detail), new { storeId, id });
    }

    [HttpPost("endpoints/{id}/tokens/{paymentHash}/revoke")]
    public async Task<IActionResult> Revoke(string storeId, string id, string paymentHash)
    {
        if (await service.Revoke(storeId, paymentHash))
            TempData[WellKnownTempData.SuccessMessage] = "Token revoked.";
        return Redirect(Url.Action(nameof(Detail), new { storeId, id }) + "#tokens");
    }

    [HttpPost("endpoints/{id}/tokens/revoke")]
    public async Task<IActionResult> RevokeByHash(string storeId, string id, string paymentHash)
    {
        if (await service.Revoke(storeId, paymentHash.Trim().ToLowerInvariant()))
            TempData[WellKnownTempData.SuccessMessage] = "Token revoked.";
        else
            TempData[WellKnownTempData.ErrorMessage] = "No active token with this payment hash in this store.";
        return Redirect(Url.Action(nameof(Detail), new { storeId, id }) + "#tokens");
    }

    [HttpPost("root-key/rotate")]
    public async Task<IActionResult> RotateRootKey(string storeId)
    {
        var key = await service.RotateRootKey(storeId);
        TempData[WellKnownTempData.SuccessMessage] = $"New root key {key.Id}. Tokens signed with older keys stay valid until they expire.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings(string storeId, long? maxPriceSats, bool? discovery)
    {
        var settings = await service.GetSettings(storeId);
        if (maxPriceSats is { } max)
        {
            if (max < 1)
            {
                TempData[WellKnownTempData.ErrorMessage] = "The maximum price must be at least 1 sat.";
                return RedirectToAction(nameof(Index), new { storeId });
            }
            settings.MaxPriceSats = max;
        }
        if (discovery is { } d)
            settings.Discovery = d;
        await service.SaveSettings(storeId, settings);
        TempData[WellKnownTempData.SuccessMessage] = discovery switch
        {
            true => "Discovery is on: /.well-known/l402.json lists this store's endpoints.",
            false => "Discovery is off.",
            null => $"Max price set to {settings.MaxPriceSats:N0} sats per challenge."
        };
        return RedirectToAction(nameof(Index), new { storeId });
    }

    static string Slugify(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
