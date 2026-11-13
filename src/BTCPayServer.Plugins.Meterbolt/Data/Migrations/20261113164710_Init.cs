using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Meterbolt.Data.Migrations
{
    [DbContext(typeof(MeterboltDbContext))]
    [Migration("20261113164710_Init")]
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "meterbolt");

            migrationBuilder.CreateTable(
                name: "daily_calls",
                schema: "meterbolt",
                columns: table => new
                {
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    calls = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_calls", x => new { x.endpoint_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "endpoints",
                schema: "meterbolt",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    store_id = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    price_sats = table.Column<long>(type: "bigint", nullable: false),
                    token_ttl_seconds = table.Column<int>(type: "integer", nullable: false),
                    single_use = table.Column<bool>(type: "boolean", nullable: false),
                    public_url = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoints", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "root_keys",
                schema: "meterbolt",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    store_id = table.Column<string>(type: "text", nullable: false),
                    secret = table.Column<byte[]>(type: "bytea", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_root_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tokens",
                schema: "meterbolt",
                columns: table => new
                {
                    payment_hash = table.Column<string>(type: "text", nullable: false),
                    token_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    store_id = table.Column<string>(type: "text", nullable: false),
                    endpoint_id = table.Column<string>(type: "text", nullable: false),
                    invoice_id = table.Column<string>(type: "text", nullable: false),
                    root_key_id = table.Column<string>(type: "text", nullable: false),
                    price_sats = table.Column<long>(type: "bigint", nullable: false),
                    single_use = table.Column<bool>(type: "boolean", nullable: false),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tokens", x => x.payment_hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_endpoints_store_id_slug",
                schema: "meterbolt",
                table: "endpoints",
                columns: new[] { "store_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_root_keys_store_id",
                schema: "meterbolt",
                table: "root_keys",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "IX_tokens_endpoint_id_created_at",
                schema: "meterbolt",
                table: "tokens",
                columns: new[] { "endpoint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tokens_invoice_id",
                schema: "meterbolt",
                table: "tokens",
                column: "invoice_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tokens_store_id_paid_at",
                schema: "meterbolt",
                table: "tokens",
                columns: new[] { "store_id", "paid_at" });
        }
    }
}
