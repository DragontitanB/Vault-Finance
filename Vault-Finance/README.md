# Vault Finance

Vault Finance is a personal finance portfolio project focused on .NET backend architecture, code quality, security, persistence, and testing.

## Current status

The project provides a .NET 10 solution, Clean Architecture, Money and Account domain types, EF Core 10 SQL Server persistence, and ASP.NET Core Identity stores with Guid keys. The API exposes process liveness and development-only OpenAPI. Application has no financial use cases yet.

The initial schema stores Identity users and accounts. Secure application-cookie and antiforgery settings are prepared, but no registration/login/logout, account CRUD, token-issuance, or financial endpoints exist. There are no account balances, deposits, withdrawals, transactions, transfers, budgets, dashboards, or frontend. Domain Money and Account were not modified for persistence.

## Prerequisites

- Stable .NET SDK 10.0.404 or a later patch in the 10.0.4xx feature band, as specified in `global.json`. Preview SDKs are disabled.
- Access to NuGet.org for the initial package restore.
- A trusted ASP.NET Core development certificate for local HTTPS.

SQL Server 2019 Express is required only for migration and opt-in database tests. The verified local instance is localhost\SQLEXPRESS with Windows authentication and database VaultFinanceDb. Ordinary domain, configuration, and HTTP tests do not require SQL Server. Node.js and Docker are not required.

## Build and test

Run these commands from the `Vault-Finance` directory containing `VaultFinance.slnx`:

```powershell
dotnet --version
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
dotnet test VaultFinance.slnx --no-build --no-restore
```

Unit tests reference Domain directly and exercise Money and Account without API hosting or infrastructure. They cover validation, normalization, value equality, exact signed arithmetic, currency compatibility, decimal limits, renaming, and archiving. Integration tests start the actual application through `WebApplicationFactory<Program>`, verify anonymous `GET /health` in Development and Production, and verify that OpenAPI is exposed only in Development. Their in-memory server uses an HTTPS request scheme but does not exercise certificate trust, TLS negotiation, or a real network socket.

Additional configuration tests verify Identity cookie/antiforgery settings, EF mappings, rejection of another database target, and liveness without a connection string even when an authentication cookie is supplied. Health short-circuits after HTTPS redirection, before Identity can consult SQL. SQL tests are skipped unless explicitly enabled; there is no EF InMemory substitute.

Only xUnit, its VSTest adapter, Microsoft.NET.Test.Sdk, and Microsoft.AspNetCore.Mvc.Testing are declared as test packages. Infrastructure uses official Microsoft.EntityFrameworkCore, Microsoft.EntityFrameworkCore.SqlServer, and Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.15. Microsoft.EntityFrameworkCore.Design 10.0.15 is private to Api for startup tooling. The repository-local dotnet-ef tool is also pinned to 10.0.15. Do not upgrade to the 11.x tool merely because tool restore advertises it.

### Optional workspace-local CLI caches

To keep CLI state and package downloads inside this directory, set the following in the same PowerShell session before any .NET commands:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.local/dotnet'
$env:NUGET_PACKAGES = Join-Path $PWD '.local/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PWD '.local/nuget/http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:VSTEST_ARTIFACTS_DIRECTORY = Join-Path $PWD '.local/vstest'
New-Item -ItemType Directory -Path $env:VSTEST_ARTIFACTS_DIRECTORY -Force | Out-Null
```

These paths are ignored by Git. Keep the variables consistent across restore, build, test, and run. DOTNET_GENERATE_ASPNET_CERTIFICATE prevents first-run certificate generation; certificate setup remains an explicit local action. VSTEST_ARTIFACTS_DIRECTORY keeps test-platform artifacts local and avoids the fallback-to-user-temporary-directory notice observed with SDK 10.0.404, including when using Microsoft.NET.Test.Sdk 18.12.0. That notice alone does not indicate a failed test.

## Secure database configuration and migrations

The API's existing UserSecretsId identifies its development configuration. Configure ConnectionStrings:DefaultConnection through your IDE's User Secrets editor or a secure environment-variable workflow. The key must select VaultFinanceDb; the local migration script additionally requires local SQLEXPRESS and Windows authentication. Never paste a real connection string into tracked files, logs, CLI arguments, or shell history. User Secrets is local development storage, not a production secret vault.

The SQL Server provider uses compatibility level 150, matching SQL Server 2019. No code calls Migrate/EnsureCreated at API startup.

From the repository directory:

```powershell
dotnet tool restore
dotnet restore VaultFinance.slnx
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\backend\scripts\Update-Database.ps1
```

RemoteSigned above applies only to the child process; it does not change permanent machine/user policy. If your organization enforces a policy that prevents execution, follow that policy and have the reviewed migration procedure run in an approved environment.

The script reads DefaultConnection without printing it, validates the target, and checks database contents. It refuses application data, foreign tables, partial/unrecognized schema, or unexpected history. A read-only master connection checks catalog existence; all migration schema changes target VaultFinanceDb. EF creates that database if it is absent. No existing database or table is dropped, truncated, or seeded.

This is a deliberately conservative script for the initial schema. An already migrated, empty matching schema is a safe no-op. Once real data or additional migrations exist, prepare a separately reviewed migration procedure rather than weakening the checks.

Migrations and the snapshot live under backend/src/VaultFinance.Infrastructure/Persistence/Migrations. Api is the startup project. To generate a future migration after an intentional model change:

```powershell
dotnet ef migrations add YourMigrationName --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --context VaultFinanceDbContext --output-dir Persistence/Migrations -- --environment Development
```

Review the generated operations before applying them. Do not use the initial-schema script unchanged for later migrations.

### Current schema and real SQL tests

The initial migration is 20261123172009_InitialPersistence. It creates dbo.Accounts, AspNetUsers, AspNetRoles, AspNetUserClaims, AspNetRoleClaims, AspNetUserLogins, AspNetUserRoles, AspNetUserTokens, and __EFMigrationsHistory.

Accounts uses Guid identifiers, nvarchar(100) names, varchar(3) uppercase DOP/USD currency, integer status, CHECK constraints, owner/status indexes, and a restricted-delete foreign key to AspNetUsers. One user can own accounts in both currencies. Money has no persistent property yet, so there is no Money table or invented balance column.

To run the real SQL tests after applying the migration, use the Windows user that can access SQL Server and the API's User Secrets:

```powershell
$env:VAULTFINANCE_SQL_TESTS = '1'
try {
    dotnet test VaultFinance.slnx --no-build --no-restore
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
}
```

The SQL fixture permits only local SQLEXPRESS/VaultFinanceDb with Windows authentication and refuses pending migrations. Each test starts a transaction, uses synthetic Guid identities and example.invalid names, and rolls back on disposal, including on assertion failure. Tests never migrate, create/drop databases, or delete existing records for cleanup.

Verified on the local SQL Server 2019 Express instance: restore succeeded; build had zero warnings/errors; all 72 unit and 22 integration test cases passed with SQL tests enabled and no skips. Database verification found compatibility 150, the initial history record at version 10.0.15, and zero rows in all eight application tables after the tests.

## Run locally over HTTPS

Check whether your machine already has a development certificate:

```powershell
dotnet dev-certs https --check
```

If a certificate is missing or untrusted, you can explicitly create/trust it with `dotnet dev-certs https --trust`. That command changes the current user's certificate store and may show an operating system confirmation. Certificate provisioning is not part of the repository setup.

Start the API:

```powershell
dotnet run --project backend/src/VaultFinance.Api --launch-profile https --no-restore
```

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and listens on HTTPS port 7143 and HTTP port 5102. HTTP requests are redirected to HTTPS. Open a second terminal:

```powershell
curl.exe https://localhost:7143/health
curl.exe https://localhost:7143/openapi/v1.json
```

On other operating systems use `curl` instead of `curl.exe`. The health response is HTTP 200 with `Content-Type: application/json`:

```json
{"status":"ok"}
```

This response reports only that the API can serve the request. It does not report database readiness or the health of future dependencies. The endpoint requires no credentials. The OpenAPI JSON document is available only in Development; an interactive Swagger UI is not installed. Stop the server with Ctrl+C.

If HTTPS startup fails, inspect the certificate check and whether ports 7143/5102 are already in use. If restore reports `NU1301`, verify access to `https://api.nuget.org/v3/index.json`; do not replace packages with preview versions or suppress restore errors.

## Layout and references

```text
VaultFinance.slnx
backend/
  src/
    VaultFinance.Domain/
    VaultFinance.Application/
    VaultFinance.Infrastructure/
    VaultFinance.Api/
  tests/
    VaultFinance.UnitTests/
    VaultFinance.IntegrationTests/
  scripts/
    Update-Database.ps1
docs/
  requirements.md
  architecture.md
  domain-model.md
  security.md
  decisions/ADR-001-modular-monolith.md
```

| Project | Responsibility | Direct project references |
| --- | --- | --- |
| Domain | Money, Account, and their business invariants | None |
| Application | Future use cases and required ports | Domain |
| Infrastructure | SQL Server DbContext, Account mapping, Identity stores, DI | Application, Domain |
| Api | HTTP endpoints and composition root | Application, Infrastructure |
| UnitTests | Isolated Money and Account behavior checks | Domain |
| IntegrationTests | HTTP/configuration checks and opt-in real SQL tests | Api, Infrastructure |

UnitTests no longer references Api; the health HTTP contract remains covered by IntegrationTests. Tests for future application use cases should reference Application when its behavior exists.

## Configuration and security

`appsettings.json` holds non-secret shared logging and host settings. `appsettings.Development.json` overrides ASP.NET Core logging for development. The API explicitly uses console logging, without depending on the Windows Event Log. The default ASP.NET Core configuration pipeline also reads environment variables and command-line arguments; no custom configuration provider is needed. `launchSettings.json` is for local tooling and is not deployment configuration.

The template's `AllowedHosts: "*"` must be narrowed through deployment configuration when real hostnames are known. HTTPS listeners/certificates and trusted proxy settings must also be supplied for deployment. Local launch settings do not secure a production installation.

Never place credentials or real connection strings in tracked settings files. DefaultConnection comes from API User Secrets or environment configuration. Files ignored as local settings are not automatically loaded. Identity stores, secure cookie options, and antiforgery middleware are prepared; no routes issue authentication cookies or antiforgery tokens. Future JSON mutation endpoints must explicitly enforce CSRF validation and resource authorization.

Local Data Protection uses the Windows profile's normal DPAPI-protected key repository. Run hosting under the same Windows identity. A restricted process may report DPAPI or key-directory access failures even though normal user hosting works; do not delete keys or weaken cookie protection as a workaround. Production requires an explicit durable key-management strategy.

## Design documentation

- [Requirements and scope](docs/requirements.md)
- [Architecture and dependency rules](docs/architecture.md)
- [Implemented and proposed domain model, with usage examples](docs/domain-model.md)
- [Planned security strategy](docs/security.md)
- [ADR-001: Modular monolith](docs/decisions/ADR-001-modular-monolith.md)

Implementation references: [ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) and [OpenAPI document generation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0).
