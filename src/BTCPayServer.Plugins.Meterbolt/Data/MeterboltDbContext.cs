#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Meterbolt.Data;

[PluginDatabase("meterbolt_migrations")]
public class MeterboltDbContext(DbContextOptions<MeterboltDbContext> options)
    : BasePluginDbContext<MeterboltDbContext>(options)
{
    public const string Schema = "meterbolt";

    public MeterboltDbContext() : this(new DbContextOptions<MeterboltDbContext>())
    {
    }

    public DbSet<PaidEndpoint> Endpoints { get; set; } = null!;
    public DbSet<RootKey> RootKeys { get; set; } = null!;
    public DbSet<IssuedToken> Tokens { get; set; } = null!;
    public DbSet<DailyCalls> DailyCalls { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema(Schema);

        b.Entity<PaidEndpoint>(e =>
        {
            e.ToTable("endpoints");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StoreId, x.Slug }).IsUnique();
        });
        b.Entity<RootKey>(e =>
        {
            e.ToTable("root_keys");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StoreId);
        });
        b.Entity<IssuedToken>(e =>
        {
            e.ToTable("tokens");
            e.HasKey(x => x.PaymentHash);
            e.HasIndex(x => x.InvoiceId).IsUnique();
            e.HasIndex(x => new { x.EndpointId, x.CreatedAt });
            e.HasIndex(x => new { x.StoreId, x.PaidAt });
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<DailyCalls>(e =>
        {
            e.ToTable("daily_calls");
            e.HasKey(x => new { x.EndpointId, x.Day });
        });

        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(SnakeCase(property.Name));
    }

    static string SnakeCase(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}

public class PaidEndpoint
{
    public string Id { get; set; } = null!;
    public string StoreId { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public long PriceSats { get; set; }
    public int TokenTtlSeconds { get; set; }
    public bool SingleUse { get; set; }
    public string? PublicUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class RootKey
{
    public string Id { get; set; } = null!;
    public string StoreId { get; set; } = null!;
    public byte[] Secret { get; set; } = null!;
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TokenStatus
{
    Pending,
    Paid,
    Expired
}

/// <summary>One challenge. Holds no buyer data: only what the invoice and the macaroon need.</summary>
public class IssuedToken
{
    /// <summary>Lowercase hex, as in the BOLT11 invoice.</summary>
    public string PaymentHash { get; set; } = null!;
    public byte[] TokenId { get; set; } = null!;
    public string StoreId { get; set; } = null!;
    public string EndpointId { get; set; } = null!;
    public string InvoiceId { get; set; } = null!;
    public string RootKeyId { get; set; } = null!;
    public long PriceSats { get; set; }
    public bool SingleUse { get; set; }
    public int Uses { get; set; }
    public TokenStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Per-endpoint call counter per UTC day, without any caller data.</summary>
public class DailyCalls
{
    public string EndpointId { get; set; } = null!;
    public DateOnly Day { get; set; }
    public long Calls { get; set; }
}
