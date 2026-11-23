# Architecture

## Current structure

Vault Finance is a modular monolith using Clean Architecture: one deployable API and inward-facing compile-time dependencies. Domain implements Money, Account, AccountStatus, and explicit domain errors. Infrastructure implements EF Core SQL Server persistence and ASP.NET Core Identity stores. Application has no use cases yet. No financial or authentication endpoints are implemented.

| Layer | Responsibility | Allowed direct dependencies |
| --- | --- | --- |
| Domain | Business concepts, value objects, and invariants | None |
| Application | Use cases, orchestration, and ports required by concrete use cases | Domain |
| Infrastructure | EF Core/SQL Server persistence, Identity stores, and service registration | Application, Domain |
| Api | HTTP transport, configuration, and dependency injection composition | Application, Infrastructure |

Api references Infrastructure to register concrete adapters at the composition root. Future endpoint behavior should call Application use cases rather than access persistence directly. Infrastructure implementations satisfy ports defined where they are needed. Domain must not reference ASP.NET Core, EF Core, Identity, or another solution project.

## Planned module boundaries

Identity, accounts, categorization, transactions/transfers, budgets, and auditing are candidate logical boundaries. Introduce feature organization inside the existing layers as use cases arrive. Identity-specific types belong in Infrastructure; the domain refers to an owner identifier without inheriting from IdentityUser.

There are no dedicated module assemblies, module loading mechanisms, inter-module events, or cross-module contracts yet. Revisit boundaries using implemented business behavior instead of adding empty abstractions now.

## HTTP foundation

The API uses minimal endpoints. GET /health calls a small typed handler and returns HTTP 200 with {"status":"ok"}. It is explicitly anonymous and short-circuits routing after HTTPS redirection, before authentication/authorization/antiforgery middleware. Even an attached authentication cookie must not trigger Identity stamp validation or a SQL query for health. No database checks or dependency readiness claims exist.

HTTPS redirection is enabled. The local https launch profile binds ports 7143 and 5102 and selects Development. OpenAPI JSON is mapped only in Development at /openapi/v1.json; no documentation UI is installed.

## Configuration

The default WebApplication builder loads shared settings, environment-specific settings, environment variables, and command-line overrides. Development also loads the API's existing UserSecretsId. DefaultConnection comes from ConnectionStrings:DefaultConnection in User Secrets or environment configuration, never tracked settings. Tracked configuration contains logging and the template host setting only. Logging providers are explicitly limited to the console.

global.json selects the installed stable SDK feature band and disallows previews. NuGet.Config declares NuGet.org as the only package source, without credentials. Package versions are explicit. Each project enables nullable reference types and implicit usings.

Production hosting, host allowlists, certificates, reverse proxy trust, exception handling policy, and deployment secrets require a later hosting design. The development profile does not provide those deployment controls.

## Testing

UnitTests references Domain directly, with no Api or Infrastructure project reference. Tests cover Money construction, normalization, precision, value equality, signed arithmetic, incompatible currencies, overflow, and exactness at decimal boundaries. Account tests cover valid creation, identifiers, name and currency validation, safe renaming, archive transitions, and rejection of edits after archiving. The original handler unit test was replaced by domain tests; the existing HTTP integration tests still protect the health contract.

IntegrationTests references Api, Infrastructure, and Microsoft.AspNetCore.Mvc.Testing. Existing WebApplicationFactory tests remain unchanged. Additional tests verify mappings, secure cookie/antiforgery options, database target validation, and health without DefaultConnection. These configuration/HTTP tests do not require SQL Server.

Opt-in SQL tests use the real local SQL Server 2019 Express instance and migrated VaultFinanceDb. They round-trip Identity users and accounts, including archived accounts and DOP/USD accounts owned by one user; verify SQL constraints, foreign keys, deletion restrictions, and immutable persisted ownership/currency; and roll back every test transaction. They never create/drop databases, migrate, use EnsureCreated, or replace SQL Server with EF InMemory. Set VAULTFINANCE_SQL_TESTS=1 to enable them.

## Persistence and materialization

Infrastructure owns VaultFinanceDbContext, an IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, and ApplicationUser, which derives from IdentityUser<Guid>. Domain references neither Identity nor EF. Official EF Core, SQL Server, Identity stores, Design tooling, and the local dotnet-ef tool are pinned to stable 10.0.15. Design is private to the API startup project; migrations and the model snapshot live under Infrastructure/Persistence/Migrations.

UseSqlServer explicitly selects compatibility level 150 for SQL Server 2019. This selects EF's generated SQL behavior; it does not upgrade the server. DefaultConnection must name VaultFinanceDb, and attempts to resolve persistence for another database are rejected. Connection configuration is evaluated when the scoped context is resolved, allowing anonymous process health to function without SQL configuration or a connection attempt.

Compatibility reference: [EF Core 10 SQL Server compatibility behavior](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes). Materialization reference: [EF constructor binding](https://learn.microsoft.com/en-us/ef/core/modeling/constructors).

AccountConfiguration explicitly maps get-only Id, UserId, and Currency so EF can bind the existing four-argument Account constructor. The constructor validates identity, ownership, name, and currency. EF then restores the persisted Status through its private setter, preserving Archived on reload. No parameterless constructor, public setters, EF annotations, or domain changes were needed.

Accounts has a caller-supplied Guid primary key, an AspNetUsers foreign key using NO ACTION/Restrict deletion, indexes on UserId and (UserId, Status), nvarchar(100) names, varchar(3) currency, and integer status. CHECK constraints enforce non-empty identifiers, trimmed non-empty names, uppercase DOP/USD, and Active/Archived values. EF rejects changes to UserId/Currency after insertion. This guards tracked updates; it is not database authorization against arbitrary privileged SQL.

A user can own multiple accounts in either supported currency. No unique-owner or single-currency-per-user constraint exists. Money is not mapped as an entity or stored independently: no current persistent property uses it. Do not invent balance columns or narrow its full decimal range to decimal(18,2). Future monetary properties require an explicit mapping and precision decision.

## Migration operations

The initial migration is 20261123172009_InitialPersistence. API startup never runs Migrate or EnsureCreated. The reviewed local Update-Database.ps1 script reads secure configuration, validates local SQLEXPRESS/Windows authentication/VaultFinanceDb, and refuses foreign tables, application data, or unexpected migration history. Its master connection is used only to read catalog existence; EF applies the schema exclusively to VaultFinanceDb and creates that database if absent.

The script is deliberately limited to this initial schema, not a general production deployment tool. Once real data or later migrations exist, use a separately reviewed deployment procedure. It performs no backup, deletion, truncation, seeding, or automatic recovery.

## Deferred implementation

Identity stores, secure application-cookie options, antiforgery services, and middleware are registered. No routes issue cookies or antiforgery tokens. Authentication flows, financial resource authorization, endpoint-specific CSRF enforcement, application use cases, production Data Protection key management, concurrency controls, idempotency, audit persistence, React, and TypeScript remain deferred.

Use direct, explicit use-case composition first. Add a port only when a real application use case needs an external capability. Do not add generic repositories, speculative service layers, messaging infrastructure, caching, or mediator packages.

See [ADR-001](decisions/ADR-001-modular-monolith.md) for rationale and tradeoffs.
