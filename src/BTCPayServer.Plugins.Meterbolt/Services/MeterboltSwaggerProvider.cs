#nullable enable
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Meterbolt.Services;

public class MeterboltSwaggerProvider : ISwaggerProvider
{
    public async Task<JObject> Fetch()
    {
        await using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("BTCPayServer.Plugins.Meterbolt.Resources.swagger.meterbolt.json")!;
        using var reader = new StreamReader(stream);
        return JObject.Parse(await reader.ReadToEndAsync());
    }
}
