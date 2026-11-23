# Vault Finance

Vault Finance is a personal finance portfolio project focused on .NET backend architecture, code quality, security, persistence, and testing.

## Current status

This first iteration provides a .NET 10 solution, Clean Architecture project references, a public process liveness endpoint, development HTTPS configuration, development-only OpenAPI, and automated tests. Domain, Application, and Infrastructure intentionally contain no business implementations.

There are no users, login flows, database connections, EF Core packages, migrations, financial operations, dashboards, or frontend yet. The documentation describes their proposed future design.

## Prerequisites

- Stable .NET SDK 10.0.404 or a later patch in the 10.0.4xx feature band, as specified in `global.json`. Preview SDKs are disabled.
- Access to NuGet.org for the initial package restore.
- A trusted ASP.NET Core development certificate for local HTTPS.

No SQL Server or Node.js installation is required for this iteration.

## Build and test

Run these commands from the `Vault-Finance` directory containing `VaultFinance.slnx`:

```powershell
dotnet --version
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
dotnet test VaultFinance.slnx --no-build --no-restore
```

The unit test calls the health handler directly. Integration tests start the actual application through `WebApplicationFactory<Program>`, verify anonymous `GET /health` in Development and Production, and verify that OpenAPI is exposed only in Development. Their in-memory server uses an HTTPS request scheme but does not exercise certificate trust, TLS negotiation, or a real network socket.

Only xUnit, its VSTest adapter, Microsoft.NET.Test.Sdk, and Microsoft.AspNetCore.Mvc.Testing are declared as test packages. Microsoft.AspNetCore.OpenApi is the API template's runtime documentation package.

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
docs/
  requirements.md
  architecture.md
  domain-model.md
  security.md
  decisions/ADR-001-modular-monolith.md
```

| Project | Responsibility | Direct project references |
| --- | --- | --- |
| Domain | Future business concepts and invariants | None |
| Application | Future use cases and required ports | Domain |
| Infrastructure | Future persistence and identity adapters | Application, Domain |
| Api | HTTP endpoints and composition root | Application, Infrastructure |
| UnitTests | Isolated behavior checks; currently the health handler | Api |
| IntegrationTests | Actual ASP.NET Core pipeline checks | Api |

The unit test references Api because the only implemented behavior is an HTTP health handler. Tests for future domain rules and application use cases should reference their corresponding projects when those behaviors exist.

## Configuration and security

`appsettings.json` holds non-secret shared logging and host settings. `appsettings.Development.json` overrides ASP.NET Core logging for development. The API explicitly uses console logging, without depending on the Windows Event Log. The default ASP.NET Core configuration pipeline also reads environment variables and command-line arguments; no custom configuration provider is needed. `launchSettings.json` is for local tooling and is not deployment configuration.

The template's `AllowedHosts: "*"` must be narrowed through deployment configuration when real hostnames are known. HTTPS listeners/certificates and trusted proxy settings must also be supplied for deployment. Local launch settings do not secure a production installation.

Never place credentials or real connection strings in tracked settings files. Future secrets will come from environment variables, explicitly configured developer User Secrets, or a deployment secret store. Files ignored as local settings are not automatically loaded. No secret storage, authentication, CSRF middleware, persistence, or financial logic is implemented yet.

## Design documentation

- [Requirements and scope](docs/requirements.md)
- [Architecture and dependency rules](docs/architecture.md)
- [Proposed domain model](docs/domain-model.md)
- [Planned security strategy](docs/security.md)
- [ADR-001: Modular monolith](docs/decisions/ADR-001-modular-monolith.md)

Implementation references: [ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) and [OpenAPI document generation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0).
