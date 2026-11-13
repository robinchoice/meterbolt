#nullable enable
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Meterbolt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Meterbolt.Controllers;

/// <summary>Public list of paid endpoints for stores that switched discovery on.</summary>
[AllowAnonymous]
[EnableCors(CorsPolicies.All)]
public class DiscoveryController(MeterboltService service) : ControllerBase
{
    [HttpGet("~/.well-known/l402.json")]
    public async Task<IActionResult> Get()
    {
        var endpoints = await service.GetDiscoverableEndpoints();
        return Ok(new JObject
        {
            ["version"] = 1,
            ["endpoints"] = new JArray(endpoints.Select(x => new JObject
            {
                ["name"] = x.Endpoint.Name,
                ["url"] = x.Endpoint.PublicUrl,
                ["store"] = x.StoreName,
                ["priceSats"] = x.Endpoint.PriceSats,
                ["tokenLifetimeSeconds"] = x.Endpoint.TokenTtlSeconds,
                ["singleUse"] = x.Endpoint.SingleUse,
                ["scheme"] = "L402"
            }))
        });
    }
}
