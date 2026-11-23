# ADR-001: Modular Monolith with Clean Architecture

- Status: Accepted
- Date: 2026-11-23

## Context

Vault Finance is a personal finance portfolio project developed incrementally. It must demonstrate maintainable .NET backend design, ownership and security rules, transactional persistence, and meaningful testing without adding operational infrastructure before a use case requires it.

Accounts, categories, transactions, transfers, and budgets share consistency requirements. The first iteration contains only the technical foundation; business boundaries are still proposals.

## Decision

Build one deployable ASP.NET Core API as a modular monolith, with Clean Architecture layers:

- Domain depends on no other solution layer.
- Application depends on Domain.
- Infrastructure depends on Application and Domain.
- Api depends on Application and Infrastructure for composition.

Organize future business capabilities as explicit logical modules/features within those layers as behavior emerges. Start with ordinary classes and explicit dependency injection. Create ports only for concrete use cases that need external capabilities.

Use .NET 10 and C#, adding EF Core 10 and SQL Server in later iterations. Keep the future React/TypeScript client separate from backend business rules.

Do not introduce microservices, RabbitMQ, Redis, MediatR, generic repositories, or speculative abstractions.

## Rationale

A single deployment and process keep local development, debugging, and operations manageable. Future SQL transactions can preserve consistency across transfer legs, audit entries, and idempotency records without distributed coordination.

Inward dependencies keep business behavior independent of HTTP, identity implementation, and persistence. The layers provide places for isolated tests and infrastructure integration tests as the application grows.

Logical modularity documents ownership of behavior without committing to additional assemblies or service boundaries before the domain is understood.

## Alternatives considered

A single undifferentiated API project is initially smaller, but makes it easier to couple business rules directly to transport and database concerns.

Microservices introduce network failures, deployment coordination, distributed consistency, and observability work without a current scaling or team requirement.

A mediator library and generic repository layer could standardize dispatch or data access, but currently add dependencies and indirection without implemented use cases to justify them.

## Consequences

The solution has more projects and explicit project references than a minimal API alone. Developers must preserve dependency direction and prevent endpoints from bypassing use cases to access infrastructure.

One process and deployment mean modules share release timing and resources. Project layering alone does not enforce business module boundaries; reviews and future tests must preserve them.

Domain, Application, and Infrastructure remain empty until concrete behavior is implemented. The initial API's health endpoint does not require domain abstractions.

## Revisit conditions

Reconsider module assemblies or service extraction only when independent deployment, ownership, scaling, or a demonstrated isolation requirement outweighs the cost. Record any change in a new ADR, supported by implemented behavior and operational evidence.
