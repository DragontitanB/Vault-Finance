# Vault Finance

Vault Finance is a personal finance portfolio project focused on .NET backend architecture, code quality, security, persistence, and testing.

## Current status

The project provides a .NET 10 solution, Clean Architecture, immutable financial domain types, EF Core 10 SQL Server account/journal storage, and ASP.NET Core Identity stores with Guid keys. The API exposes process liveness and development-only OpenAPI. Application supports initial opening, Income, Expense, internal Transfer, complete Reversal, atomic opening correction and owner-scoped derived-balance reads, without financial HTTP endpoints.

The API implements registration, cookie login/logout, current-user lookup, CSRF bootstrap, and owner-scoped account creation/listing/detail/rename/archive. Identity owns password validation/hashing, normalization, and temporary lockout. Initial opening, Income, Expense, internal Transfer, complete Reversal and atomic opening correction use the same controlled SQL writer with exact funds/headroom validation, canonical idempotency and audit. Transfer records one canonical operation with two opposite effects, locking both accounts sequentially in native SQL Guid order. Financial HTTP endpoints, budgets, dashboard and frontend remain unimplemented. Opening correction uses expected revisions and commits compensation/replacement with a new version, validating the complete final balance; unchanged amounts reject without a new version. Reversals derive exact inverse effects from persisted Income/Expense/Transfer originals, including on archived accounts when final bounds permit. Domain and previous migrations remain unchanged.

ADR-002 is Accepted. Financial amounts are positive and bounded by decimal(28,2), using DOP/USD. Confirmed descriptors have no mutation/confirmation-again API; reversal references the original without editing it. UTC recording metadata derives an America/Santo_Domingo effective date. Creating a descriptor does not prove funds, global reversal uniqueness or SQL commit. See the [financial engine contract](docs/financial-engine-design.md) and [domain usage examples](docs/domain-model.md).

Current validation: restore succeeded; build had zero warnings/errors; all 228 unit and 468 integration cases passed, with zero skips (696 total). All 605 prior cases are retained, with 21 new Application unit and 70 new real SQL cases. FinancialOpeningCorrectionWriter was applied only to the marked VaultFinanceJournalTests database; independent read-only before/after checks confirm VaultFinanceDb still has InitialPersistence only and unchanged object metadata/row counts. See [schema, constraints, SQL test safety and controlled migration commands](docs/financial-journal-persistence.md).

See the [Income/Expense contracts, funds/archiving concurrency evidence and commands](docs/financial-income-expense.md), plus the retained [opening contract](docs/financial-opening-writer.md).

See [internal transfers](docs/financial-transfers.md), [complete reversals](docs/financial-reversals.md) and [atomic opening corrections](docs/financial-opening-corrections.md) for contracts, exact effects, versioning, archive policy, SQL concurrency evidence and limits.

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

Unit tests reference Domain and Application, without API hosting or Infrastructure. They exercise domain invariants and opening validation/trusted-owner context through a narrow fake journal port. They cover validation, normalization, value equality, exact signed arithmetic, currency compatibility, decimal limits, renaming/archiving, operation shapes, recording dates, causal reversal references and immutability. Integration tests start the actual application through `WebApplicationFactory<Program>`, verify anonymous `GET /health` in Development and Production, and verify that OpenAPI is exposed only in Development. Their in-memory server uses an HTTPS request scheme but does not exercise certificate trust, TLS negotiation, or a real network socket.

Set `$env:VAULTFINANCE_SQL_TESTS = '0'` for tests that do not require SQL. SQL facts/theories are explicitly skipped in that mode. The previous Domain-only run had 126 unit and 29 ordinary integration passes, plus 32 skipped SQL methods; enabling those theories expands their cases. The current full SQL-enabled result is reported above.

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

Accounts uses Guid identifiers, nvarchar(100) names, varchar(3) uppercase DOP/USD currency, integer status, CHECK constraints, owner/status indexes, and a restricted-delete foreign key to AspNetUsers. One user can own accounts in both currencies. The new journal migration adds only an Accounts ownership/currency alternate key, five financial storage tables, append-only triggers and a read-only effects view. There is no Money table or balance column.

SQL tests require the separate ConnectionStrings:SqlTestConnection in API User Secrets or ConnectionStrings__SqlTestConnection in the environment. It must target exactly the local, project-owned VaultFinanceJournalTests database, never VaultFinanceDb. Preserve approved SQL transport security settings; no connection value is committed. For the first explicit test-database creation:

```powershell
$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '1'
try {
    dotnet test VaultFinance.slnx --no-build --no-restore
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
```

For later runs, omit the creation flag. The fixture verifies the database ownership marker, rejects foreign tables/history, and applies migrations only to that dedicated database. Most tests roll back synthetic data. One history test commits a minimal synthetic group and verifies preservation through another connection; those append-only records remain isolated in the test database. No database is dropped/reset or existing data deleted for cleanup. See the [complete lifecycle and optional test script](docs/financial-journal-persistence.md).

Authentication HTTP tests use WebApplicationFactory with the real SQL provider and a shared transaction across the request scopes of each test. Passwords and email addresses are generated synthetically; every test rolls back, including on failure. The test-only Data Protection provider is ephemeral and does not replace production key storage or certificate validation.

The account suite uses two independent authenticated cookie jars per test against real SQL Server, with the same rollback isolation. It covers foreign-ID reads/writes, owner-only lists, strict DTOs, domain validation, archive idempotence, CSRF, paging, sorting, and a stale-snapshot rename after archive. The previous ownership/currency persistence assertion now includes the earlier EF alternate-key rejection.

Historical account-management validation was 72 unit + 109 integration = 181 passes. Journal persistence passed 302 cases, opening passed 369 and Income/Expense passed 453. Transfer validation passed 528. Reversal validation passed 605. Current validation passes 696, adding versioned atomic opening corrections, negative-prefix/final-balance checks, mixed writers, blocked balance visibility, archive policy and staged rollback. Physical network-loss/native-deadlock fault injection remains outside this increment. Concurrent independent pairs can still wait on shared journal-index resources; no parallel-throughput or deadlock-free guarantee is claimed.

## Authentication and CSRF

| Endpoint | Success | Requirements |
| --- | --- | --- |
| POST /api/v1/auth/register | 201 with id/email; no automatic login | Anonymous CSRF pair; 3 requests/minute/IP |
| POST /api/v1/auth/login | 200 with id/email; secure session cookie | CSRF pair; 5 requests/minute/IP |
| POST /api/v1/auth/logout | 204; browser cookie removed | Authentication and fresh identity-bound CSRF pair |
| GET /api/v1/auth/me | 200 with id/email only | Authentication; otherwise 401 |
| GET /api/v1/auth/csrf | 200 with a request token | Same browser cookie jar; anonymous or authenticated |

Get /csrf first, retain its JSON token in memory, and send X-CSRF-TOKEN on every mutation using the same browser cookie jar. Fetch /csrf again after login and logout: the token is tied to the current identity, and those transitions clear the antiforgery cookie. Never place credentials or session cookies in local storage.

The application cookie is HttpOnly, Secure, SameSite=Lax, host-only, Path=/, and non-persistent, with a 30-minute sliding ticket lifetime. The antiforgery cookie is HttpOnly, Secure, and SameSite=Strict. All authentication responses are no-store. No JWT or permissive CORS policy is used.

Registration trims email, uses Identity normalization, and rejects normalized duplicates with 409. Passwords require 15–128 characters and at least four distinct characters; long passphrases are supported. Login failures use a generic 401 for unknown users, incorrect passwords, and locked accounts. Ten failures cause a short one-minute account lockout. Rate limiting is per process and direct transport IP; distributed abuse and production proxy configuration require additional deployment controls.

Errors use ProblemDetails: 400 for validation/CSRF, 401 for failed authentication, 409 for duplicate email, and 429 for rate limiting. A CSRF failure includes code invalid_csrf_token; login failure includes invalid_credentials. Duplicate registration intentionally has a distinct conflict response. Email confirmation, recovery, MFA, global session revocation, and financial resource authorization remain future work.

Every matched mutating /api route is protected by the global CSRF guard, including anonymous registration/login. Future endpoints must remain under /api, explicitly require authorization where appropriate, and add missing/invalid/valid token tests. The guard does not rely on form metadata or a frontend convention.

### Reproducible HTTPS requests without saved credentials

Start the API as described below. This PowerShell example uses one cookie jar, prompts for a new synthetic password, and keeps passwords/tokens in memory rather than files or literal command history. It creates one disposable example.invalid user and an archived demo account; unlike the automated suite, manual requests persist those records. Do not use real credentials.

```powershell
$base = 'https://localhost:7143/api/v1/auth'
$email = 'demo-' + [Guid]::NewGuid().ToString('N') + '@example.invalid'
$securePassword = Read-Host 'New demo passphrase (15-128 characters, 4 distinct)' -AsSecureString
$password = [Net.NetworkCredential]::new('', $securePassword).Password
$body = @{ email = $email; password = $password } | ConvertTo-Json

$csrf = Invoke-RestMethod -Uri "$base/csrf" -SessionVariable browser
$headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
$registered = Invoke-RestMethod -Uri "$base/register" -Method Post -WebSession $browser -Headers $headers -ContentType 'application/json' -Body $body
$signedIn = Invoke-RestMethod -Uri "$base/login" -Method Post -WebSession $browser -Headers $headers -ContentType 'application/json' -Body $body

# Refresh after the identity changed. Do not print the token or cookies.
$csrf = Invoke-RestMethod -Uri "$base/csrf" -WebSession $browser
$headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
$me = Invoke-RestMethod -Uri "$base/me" -WebSession $browser
$me | Select-Object id, email

# All account ownership comes from this authenticated session.
$accounts = 'https://localhost:7143/api/v1/accounts'
$createdAccount = Invoke-RestMethod -Uri $accounts -Method Post -WebSession $browser -Headers $headers -ContentType 'application/json' -Body (@{ name = 'Demo cash'; currency = 'DOP' } | ConvertTo-Json)
$accountUri = "$accounts/$($createdAccount.id)"
$page = Invoke-RestMethod -Uri ($accounts + '?page=1&pageSize=20&status=Active&sort=name') -WebSession $browser
$detail = Invoke-RestMethod -Uri $accountUri -WebSession $browser
$renamed = Invoke-RestMethod -Uri $accountUri -Method Patch -WebSession $browser -Headers $headers -ContentType 'application/json' -Body (@{ name = 'Demo emergency fund' } | ConvertTo-Json)
$archived = Invoke-RestMethod -Uri ($accountUri + '/archive') -Method Post -WebSession $browser -Headers $headers -ContentType 'application/json' -Body '{}'
$archivedAgain = Invoke-RestMethod -Uri ($accountUri + '/archive') -Method Post -WebSession $browser -Headers $headers -ContentType 'application/json' -Body '{}'
$archivedAgain | Select-Object id, name, currency, status

Invoke-WebRequest -Uri "$base/logout" -Method Post -WebSession $browser -Headers $headers -UseBasicParsing | Out-Null
$csrf = Invoke-RestMethod -Uri "$base/csrf" -WebSession $browser
$password = $null
$body = $null
$headers.Clear()
$securePassword.Dispose()
```

A new browser session without cookies receives 401 from /me. Omitting the token header or sending an old token after an identity change produces 400. No certificate-validation bypass is needed. For cookie flags and rejected-token examples without persisted demo users, run the automated integration suite.

## Account management

All routes require a cookie-authenticated user. An anonymous account request returns 401; authenticated POST/PATCH requests require a valid X-CSRF-TOKEN and matching cookie. The registration/login/logout CSRF behavior is unchanged.

| Endpoint | Input | Success |
| --- | --- | --- |
| POST /api/v1/accounts | JSON name and currency (DOP/USD) | 201 with DTO and Location |
| GET /api/v1/accounts | Optional page/pageSize/status/sort | 200 with owned page |
| GET /api/v1/accounts/{id} | Owned account identifier | 200 with DTO |
| PATCH /api/v1/accounts/{id} | JSON name only | 200 with renamed DTO |
| POST /api/v1/accounts/{id}/archive | No body or empty JSON object | 200 with archived DTO; repeat is idempotent |

Account DTOs contain only id, name, currency, and status (Active/Archived). There is no balance or owner field. Names are trimmed and limited to 100 UTF-16 code units; currencies are normalized using the existing domain rules. Unknown JSON fields are rejected, including ownerId, userId, id, status, balances, and currency on rename. Currency and ownership cannot be changed.

Every ID operation includes the authenticated owner in the SQL predicate. A foreign account and a missing account both produce 404 account_not_found, with no disclosure of the foreign account's state. Invalid names/currencies produce ValidationProblem 400; renaming an owned archived account produces 409 account_archived. No DELETE endpoint exists, and archiving retains the row.

List defaults are page=1, pageSize=20, all states, and sort=name. Valid page range is 1–10000 and pageSize is 1–100. status accepts Active or Archived case-insensitively; sort accepts name or name_desc. Unknown/repeated query parameters are rejected. Other account routes accept no query parameters. A list response has items, page, pageSize, and owner/status-filtered totalCount.

Ordering uses database collation for Name and ascending Id as the unique tie-breaker. Reads are AsNoTracking DTO projections. A conditional rename updates Name only while the stored account is still Active, preventing a stale snapshot from editing after archive. Active-account concurrent renames remain last-write-wins; count/page are separate reads, so offset pagination is not a snapshot under concurrent changes.

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
  financial-engine-design.md
  financial-journal-persistence.md
  financial-opening-writer.md
  financial-income-expense.md
  financial-transfers.md
  financial-reversals.md
  financial-opening-corrections.md
  decisions/ADR-001-modular-monolith.md
  decisions/ADR-002-financial-operation-journal.md
```

| Project | Responsibility | Direct project references |
| --- | --- | --- |
| Domain | Money, Account, immutable financial descriptors and local invariants | None |
| Application | Authentication/account contracts, opening/correction, Income/Expense/Transfer/Reversal and derived-balance use cases | Domain |
| Infrastructure | SQL Server storage, shared transactional financial protocol and Identity | Application, Domain |
| Api | HTTP endpoints and composition root | Application, Infrastructure |
| UnitTests | Domain invariants and isolated opening/correction/entry/transfer/reversal Application behavior | Domain, Application |
| IntegrationTests | HTTP/configuration checks and opt-in real SQL tests | Api, Infrastructure |

UnitTests references Domain and Application, without Api/Infrastructure. The health HTTP contract remains covered by IntegrationTests.

## Configuration and security

`appsettings.json` holds non-secret shared logging and host settings. `appsettings.Development.json` overrides ASP.NET Core logging for development. The API explicitly uses console logging, without depending on the Windows Event Log. The default ASP.NET Core configuration pipeline also reads environment variables and command-line arguments; no custom configuration provider is needed. `launchSettings.json` is for local tooling and is not deployment configuration.

The template's `AllowedHosts: "*"` must be narrowed through deployment configuration when real hostnames are known. HTTPS listeners/certificates and trusted proxy settings must also be supplied for deployment. Local launch settings do not secure a production installation.

Never place credentials or real connection strings in tracked settings files. DefaultConnection comes from API User Secrets or environment configuration. Files ignored as local settings are not automatically loaded. Authentication issues secure cookies and CSRF tokens; no request/response body logging is enabled, credential DTOs do not print their contents, and sensitive EF logging remains disabled.

Data Protection uses application name VaultFinance and the Windows profile's normal DPAPI-protected local key repository. Run hosting under the same Windows identity. Production requires a protected durable key repository shared by instances of the same deployment, isolated by environment, with rotation, old-key retention, backup, and recovery procedures. Do not delete keys or use ephemeral production keys to mask permissions problems. Logout removes the browser cookie; it does not revoke independently copied cookies on other clients.

## Design documentation

- [Requirements and scope](docs/requirements.md)
- [Architecture and dependency rules](docs/architecture.md)
- [Implemented and proposed domain model, with usage examples](docs/domain-model.md)
- [Planned security strategy](docs/security.md)
- [ADR-001: Modular monolith](docs/decisions/ADR-001-modular-monolith.md)
- [Accepted financial engine contracts and implementation increments](docs/financial-engine-design.md)
- [Implemented journal schema, dedicated SQL tests and migration commands](docs/financial-journal-persistence.md)
- [Transactional opening, canonical idempotency and derived-balance protocol](docs/financial-opening-writer.md)
- [Income/Expense, funds validation and real archive/concurrency proof](docs/financial-income-expense.md)
- [Internal transfers, deterministic account locks and conservation](docs/financial-transfers.md)
- [Complete reversals, immutable originals and concurrent uniqueness](docs/financial-reversals.md)
- [Atomic opening corrections, expected revisions and final-batch validation](docs/financial-opening-corrections.md)
- [ADR-002: Immutable financial operation journal](docs/decisions/ADR-002-financial-operation-journal.md)

Implementation references: [ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) and [OpenAPI document generation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0).
