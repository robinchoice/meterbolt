#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Plugins.Meterbolt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Meterbolt.Controllers;

public class VerifyRequest
{
    public string? Authorization { get; set; }
    /// <summary>Optional: the endpoint slug the caller expects the token to be for.</summary>
    public string? Endpoint { get; set; }
}

[ApiController]
[EnableCors(CorsPolicies.All)]
public class GreenfieldMeterboltController(MeterboltService service) : ControllerBase
{
    [HttpPost("~/api/v1/stores/{storeId}/l402/endpoints/{slug}/challenges")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield, Policy = MeterboltPolicies.CanIssue)]
    public async Task<IActionResult> CreateChallenge(string storeId, string slug, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        var endpoint = await service.GetEndpointBySlug(store.Id, slug);
        if (endpoint is null)
            return this.CreateAPIError(404, "endpoint-not-found", "No paid endpoint with this slug in this store.");
        try
        {
            var c = await service.CreateChallenge(store, endpoint, Request.GetAbsoluteRoot(), cancellationToken);
            return StatusCode(201, new JObject
            {
                ["macaroon"] = c.Macaroon,
                ["invoice"] = c.Invoice,
                ["paymentHash"] = c.PaymentHash,
                ["invoiceId"] = c.InvoiceId,
                ["amountSats"] = c.AmountSats,
                ["invoiceExpiresAt"] = c.InvoiceExpiresAt.ToUnixTimeSeconds(),
                ["expiresAt"] = c.ExpiresAt.ToUnixTimeSeconds(),
                ["wwwAuthenticate"] = c.WwwAuthenticate
            });
        }
        catch (ChallengeException e)
        {
            return this.CreateAPIError(e.Code == "endpoint-disabled" ? 409 : 400, e.Code, e.Message);
        }
    }

    [HttpPost("~/api/v1/stores/{storeId}/l402/verify")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield, Policy = MeterboltPolicies.CanVerify)]
    public async Task<IActionResult> Verify(string storeId, [FromBody] VerifyRequest? request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Authorization))
        {
            ModelState.AddModelError(nameof(VerifyRequest.Authorization), "The L402 Authorization header value is required.");
            return this.CreateValidationError(ModelState);
        }
        var store = HttpContext.GetStoreData();
        var result = await service.Verify(store.Id, request.Authorization, request.Endpoint, cancellationToken);
        if (!result.Valid)
            return Ok(new JObject { ["valid"] = false, ["reason"] = result.Reason });
        return Ok(new JObject
        {
            ["valid"] = true,
            ["endpoint"] = result.Endpoint!.Slug,
            ["paymentHash"] = result.Token!.PaymentHash,
            ["expiresAt"] = result.Token.ExpiresAt.ToUnixTimeSeconds(),
            ["singleUse"] = result.Token.SingleUse,
            ["uses"] = result.Token.Uses
        });
    }
}
