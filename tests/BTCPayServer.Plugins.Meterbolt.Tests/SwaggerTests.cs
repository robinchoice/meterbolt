using BTCPayServer.Plugins.Meterbolt.Services;

namespace BTCPayServer.Plugins.Meterbolt.Tests;

[Trait("Fast", "Fast")]
public class SwaggerTests
{
    [Fact]
    public async Task OpenApiFragmentIsEmbedded()
    {
        var doc = await new MeterboltSwaggerProvider().Fetch();
        Assert.NotNull(doc["paths"]!["/api/v1/stores/{storeId}/l402/endpoints/{slug}/challenges"]);
        Assert.NotNull(doc["paths"]!["/api/v1/stores/{storeId}/l402/verify"]);
        Assert.NotNull(doc["components"]!["schemas"]!["MeterboltChallenge"]);
    }
}
