# Architecture

## Current structure

Vault Finance is a modular monolith using Clean Architecture: one deployable API and inward-facing compile-time dependencies. Domain currently implements Money, Account, AccountStatus, and explicit domain errors. Application and Infrastructure have no behavior yet. No business module assemblies, application ports, persistence adapters, or financial endpoints are implemented.

| Layer | Responsibility | Allowed direct dependencies |
| --- | --- | --- |
| Domain | Business concepts, value objects, and invariants | None |
| Application | Use cases, orchestration, and ports required by concrete use cases | Domain |
| Infrastructure | EF Core/SQL Server persistence, Identity integration, and other adapters when needed | Application, Domain |
| Api | HTTP transport, configuration, and dependency injection composition | Application, Infrastructure |

Api references Infrastructure to register concrete adapters at the composition root. Future endpoint behavior should call Application use cases rather than access persistence directly. Infrastructure implementations satisfy ports defined where they are needed. Domain must not reference ASP.NET Core, EF Core, Identity, or another solution project.

## Planned module boundaries

Identity, accounts, categorization, transactions/transfers, budgets, and auditing are candidate logical boundaries. Introduce feature organization inside the existing layers as use cases arrive. Identity-specific types belong in Infrastructure; the domain refers to an owner identifier without inheriting from IdentityUser.

There are no dedicated module assemblies, module loading mechanisms, inter-module events, or cross-module contracts yet. Revisit boundaries using implemented business behavior instead of adding empty abstractions now.

## HTTP foundation

The API uses minimal endpoints. GET /health calls a small typed handler and returns HTTP 200 with {"status":"ok"}. It is explicitly anonymous and reports only process liveness. No database checks or dependency readiness claims exist.

HTTPS redirection is enabled. The local https launch profile binds ports 7143 and 5102 and selects Development. OpenAPI JSON is mapped only in Development at /openapi/v1.json; no documentation UI is installed.

## Configuration

The default WebApplication builder loads shared settings, environment-specific settings, environment variables, and command-line overrides. Tracked configuration contains logging and the template host setting only. Development overrides framework logging. Logging providers are explicitly limited to the console, keeping local execution independent of Windows Event Log permissions. No connection strings or credentials are configured.

global.json selects the installed stable SDK feature band and disallows previews. NuGet.Config declares NuGet.org as the only package source, without credentials. Package versions are explicit. Each project enables nullable reference types and implicit usings.

Production hosting, host allowlists, certificates, reverse proxy trust, exception handling policy, and deployment secrets require a later hosting design. The development profile does not provide those deployment controls.

## Testing

UnitTests references Domain directly, with no Api or Infrastructure project reference. Tests cover Money construction, normalization, precision, value equality, signed arithmetic, incompatible currencies, overflow, and exactness at decimal boundaries. Account tests cover valid creation, identifiers, name and currency validation, safe renaming, archive transitions, and rejection of edits after archiving. The original handler unit test was replaced by domain tests; the existing HTTP integration tests still protect the health contract.

IntegrationTests references Api and Microsoft.AspNetCore.Mvc.Testing. WebApplicationFactory starts the real application pipeline in memory. Tests verify the health HTTP contract without credentials in Development and Production, plus development-only OpenAPI exposure. These tests do not validate network listeners, TLS, SQL Server, or unimplemented business rules.

## Deferred implementation

EF Core 10, SQL Server, Identity, resource authorization, CSRF validation, use cases, transaction boundaries, concurrency controls, idempotency storage, and audit persistence are deferred. React and TypeScript are also deferred.

Use direct, explicit use-case composition first. Add a port only when a real application use case needs an external capability. Do not add generic repositories, speculative service layers, messaging infrastructure, caching, or mediator packages.

See [ADR-001](decisions/ADR-001-modular-monolith.md) for rationale and tradeoffs.
