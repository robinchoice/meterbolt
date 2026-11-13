using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Meterbolt.Data;
using BTCPayServer.Plugins.Meterbolt.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Meterbolt;

public class Plugin : BaseBTCPayServerPlugin
{
    public const string Area = "Meterbolt";
    public const string ViewsDirectory = "/Plugins/" + Area + "/Views";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddPluginDbContext<MeterboltDbContext>();
        services.AddScoped<MeterboltService>();
        services.AddSingleton<IHostedService, InvoiceListener>();
        services.AddSingleton<ISwaggerProvider, MeterboltSwaggerProvider>();
        services.AddUIExtension("store-integrations-nav", $"{ViewsDirectory}/NavExtension.cshtml");

        services.AddPolicyDefinitions(
            new PolicyDefinition(MeterboltPolicies.CanIssue,
                new PermissionDisplay("Issue L402 challenges", "Allows creating L402 challenges (a Lightning invoice and a macaroon) on all your stores."),
                new PermissionDisplay("Issue L402 challenges", "Allows creating L402 challenges (a Lightning invoice and a macaroon) on the selected stores."),
                includedByPermissions: [Policies.CanModifyStoreSettings]),
            new PolicyDefinition(MeterboltPolicies.CanVerify,
                new PermissionDisplay("Verify L402 tokens", "Allows verifying L402 tokens on all your stores."),
                new PermissionDisplay("Verify L402 tokens", "Allows verifying L402 tokens on the selected stores."),
                includedByPermissions: [Policies.CanModifyStoreSettings]));

        // Bounded storage: unpaid challenges go after two days, used-up tokens after 90 days,
        // call counters after 90 days. Paid invoices stay in BTCPay as usual.
        services.AddScheduledDbScript("Meterbolt cleanup",
            """
            WITH deleted_tokens AS (
                DELETE FROM meterbolt.tokens
                WHERE (status <> 'Paid' AND created_at < @now - Interval '2 days')
                   OR expires_at < @now - Interval '90 days'
                RETURNING 1
            ),
            deleted_calls AS (
                DELETE FROM meterbolt.daily_calls
                WHERE day < (@now - Interval '90 days')::date
                RETURNING 1
            )
            SELECT (SELECT COUNT(*) FROM deleted_tokens) + (SELECT COUNT(*) FROM deleted_calls);
            """);
    }
}
