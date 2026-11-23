# Requirements

## Product purpose

Vault Finance will help individuals record and understand their own finances. The portfolio emphasizes a maintainable .NET backend with explicit ownership, financial invariants, persistence, and meaningful tests.

## Iteration 1: implemented foundation

- A stable .NET 10 solution in .slnx format with four production projects and two xUnit projects.
- Clean Architecture dependencies and no business implementations in the inner layers.
- Anonymous GET /health returning only process liveness.
- Development HTTPS and development-only OpenAPI JSON.
- A direct handler unit test and real application integration tests using WebApplicationFactory.
- Initial requirements, architecture, domain model, security strategy, and an architectural decision record.

This iteration does not implement the future MVP below.

## Proposed MVP: future work

1. Personal identity: register, log in, and log out through ASP.NET Core Identity and secure cookie sessions.
2. Accounts: create and rename owned accounts, record an opening balance, and archive accounts without losing history.
3. Categories: manage owned income and expense categories and archive referenced categories.
4. Transactions: record, list, filter, correct, and delete owned income and expense entries; calculate balances consistently.
5. Transfers: move money atomically between two active accounts owned by the same user, without classifying the movement as income or expense.
6. Budgets: set monthly spending limits per expense category and report actual spending against those limits.
7. Summary: expose basic balances, income, expense, and budget totals through the backend. A React and TypeScript dashboard will consume these endpoints in a later iteration.
8. Accountability: audit relevant changes and protect retryable financial write operations with idempotency.
9. Persistence: introduce EF Core 10, SQL Server, schema migrations, and database-backed integration tests when the relevant use cases are implemented.

The initial MVP proposes one reporting currency per user. All their accounts, transactions, transfers, and budgets must use that currency. Currency selection and supported precision need an explicit decision before financial implementation.

## Quality requirements

- Domain remains independent of frameworks, HTTP, persistence, and identity implementation details.
- Each owned resource is authorized against the authenticated user; a valid identifier alone grants no access.
- Money uses decimal arithmetic with explicit currency and precision rules.
- Transfers, balance-affecting writes, idempotency records, and their audit records have defined transactional boundaries.
- Validate request shape at the HTTP boundary and business rules inside use cases/domain behavior.
- Tests verify invariants, authorization failures, persistence behavior, and retry/concurrency behavior as these features arrive.
- Source control contains no secrets, certificates, real connection strings, or personal finance data.

## Outside the MVP

Bank synchronization, payment execution, investment portfolios, loans, tax calculations, multiple reporting currencies, exchange rates, cross-currency transfers, recurring transaction automation, shared accounts, organizations, social features, native mobile clients, and external notification integrations are excluded.

Microservices, RabbitMQ, Redis, MediatR, generic repositories, and speculative abstractions are not part of the initial architecture.

## Foundation acceptance

Restore, build, and tests must pass with the configured stable SDK. The API must return HTTP 200 and exactly the documented health payload, OpenAPI must be limited to Development, and production references must follow the dependency rules. No persistence or financial functionality may be claimed as complete.
