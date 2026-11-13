# AGENTS.md

## Zweck & Links

Meterbolt is a BTCPay Server plugin that sells API calls over Lightning with L402. Apps call two Greenfield endpoints (create challenge, verify token); each challenge is a normal Lightning-only BTCPay invoice. Store UI: *Plugins → L402 Paid APIs*.

- Repo: https://github.com/robinchoice/meterbolt (public, MIT), created from `btcpayserver/btcpayserver-plugin-template`
- BTCPay plugin docs: `submodules/btcpayserver/docs/developers/plugins/`
- L402: https://docs.lightning.engineering/the-lightning-network/l402, macaroon format compatible with Aperture (V2 binary, identifier `version|payment_hash|token_id`, caveats `services=<slug>:0`, `<slug>_valid_until=<unix>`)
- Code map: `L402/` (macaroon, header parsing, caveats; pure, unit-tested), `Services/MeterboltService.cs` (challenges, verify, root keys, stats), `Data/` (own EF context, schema `meterbolt`), `Controllers/` (Greenfield API, store UI, `/.well-known/l402.json`), `Resources/swagger.meterbolt.json` (OpenAPI fragment merged into `/docs`)

## Checks

No local .NET: `./dotnet-docker.sh <args>` runs the SDK in `mcr.microsoft.com/dotnet/sdk:10.0` with the host network and a NuGet cache volume.

```sh
# regtest dependencies (bitcoind, NBXplorer, Postgres, CLN, LND) – needed for integration tests and migrations
(cd submodules/btcpayserver/BTCPayServer.Tests && docker compose -p meterbolt up -d dev)

./dotnet-docker.sh build tests/BTCPayServer.Plugins.Meterbolt.Tests
./dotnet-docker.sh test --project tests/BTCPayServer.Plugins.Meterbolt.Tests --no-build --filter-trait "Fast=Fast"
./dotnet-docker.sh test --project tests/BTCPayServer.Plugins.Meterbolt.Tests --no-build --filter-trait "Integration=Integration"
./dotnet-docker.sh build src/BTCPayServer.Plugins.Meterbolt -c Release
```

CI (`.github/workflows/ci.yml`) runs the same on every push to `main`. Tests are regtest only.

Local BTCPay with the plugin (http://localhost:14142, own database so test users don't interfere):

```sh
./plugin-register.sh   # writes submodules/btcpayserver/BTCPayServer/appsettings.dev.json
DOCKER_ARGS="-d --name meterbolt-btcpay" ./dotnet-docker.sh run --project submodules/btcpayserver/BTCPayServer \
  --launch-profile Bitcoin -- --postgres "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=meterbolt_dev"
```

Pay regtest invoices with `docker exec meterbolt-customer_lightningd-1 lightning-cli --network regtest pay <bolt11>`. Razor views are compiled: rebuild and restart the container after view changes.

New migration (model change): `./dotnet-docker.sh ef migrations add <Name> --project src/BTCPayServer.Plugins.Meterbolt --startup-project tests/BTCPayServer.Plugins.Meterbolt.Tests --context MeterboltDbContext --output-dir Data/Migrations`, then follow `data-migrations.md` (move the Designer attributes into the migration, delete the Designer file and `Down()`). Never edit a shipped migration.

## Deploy

Nothing is deployed from here. Releases go through the BTCPay Plugin Builder (https://plugin-builder.btcpayserver.org); registering the plugin there, cutting a release or announcing it is Robin's decision. Bump `<Version>` in the plugin csproj and the `PluginDependency` condition in `Plugin.cs` together with the submodule tag.

## Fallen

- `submodules/btcpayserver` is pinned to tag `v2.4.7`. Updating it means: checkout the new tag, adjust `Condition` in `Plugin.cs`, check EF/CodeAnalysis versions in both csproj files, run all tests.
- The plugin's design-time packages need `Microsoft.CodeAnalysis.*` 5.9.0 pinned, otherwise restore fails with NU1107.
- Tests load the plugin through `DEBUG_PLUGINS` in `appsettings.dev.json`, written into the test output folder by a target in the test csproj. Without it the test server starts without Meterbolt.
- On a regtest environment reused for many runs, BTCPay's `EnsureChannelsSetup` occasionally fails with "Already paid this invoice successfully". Rerun; CI starts fresh.
- Integration tests and the dev instance share the regtest Postgres; the dev instance therefore uses its own database (`meterbolt_dev`).
- `uint256.ToString()` of NBitcoin gives the payment hash in BOLT11 order; `ToBytes()` does not. Hashes are stored as lowercase hex.
- The macaroon reference vector in `MacaroonTests` comes from pymacaroons 0.13 (V2 binary). pymacaroons writes an empty location field, we omit it; both parse.
- Verify returns HTTP 200 with `valid: false` and a `reason` for rejected tokens; only a missing `authorization` value is a 422.

## Abweichungen vom Standard

- C#/.NET from the BTCPay plugin template instead of `robinchoice/starter` (Bun/Hono/SvelteKit): BTCPay plugins are .NET assemblies.
- Texts use BTCPay's own translation system (`text-translate`, `StringLocalizer`, policy texts via `AddPolicyDefinitions`) instead of starter's DE/EN switch; translations are managed per server in BTCPay.
- Payments are BTCPay invoices themselves, not starter's payment module.
- No signup, accounts or `SIGNUP_ALLOWLIST`: access is BTCPay's store permissions.
- Permission names follow the spec (`btcpay.store.canissuel402`, `btcpay.store.canverifyl402`) like BTCPay's built-in plugins; BTCPay's plugin docs recommend the prefix `btcpay.plugin.store.` for third-party plugins.
