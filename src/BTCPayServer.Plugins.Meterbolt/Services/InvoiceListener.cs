#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Meterbolt.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Meterbolt.Services;

/// <summary>Keeps token status in step with the BTCPay invoice behind each challenge.</summary>
public class InvoiceListener(
    EventAggregator eventAggregator,
    IDbContextFactory<MeterboltDbContext> dbFactory,
    ILogger<InvoiceListener> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents() => Subscribe<InvoiceEvent>();

    protected override Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceEvent { Invoice: var invoice } && invoice.InternalTags.Contains(MeterboltService.InvoiceTag))
            return MeterboltService.MarkInvoice(dbFactory, invoice.Id, invoice.Status);
        return Task.CompletedTask;
    }
}
