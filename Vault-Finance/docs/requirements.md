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

The first iteration did not implement the future MVP below. The following domain iteration adds only the primitives listed here, without application use cases or financial endpoints.

## Iteration 2: implemented domain primitives

- Immutable Money with decimal amounts, normalized DOP/USD currencies, value equality, and exact same-currency addition/subtraction.
- At most two effective decimal places; zero and negative amounts are allowed. Excess precision, overflow, and arithmetic precision loss are rejected instead of rounded.
- Account with non-empty Guid identity and owner, a trimmed 1-to-100-character name, immutable ownership/currency, and Active/Archived states.
- Validated renaming for active accounts and idempotent archiving. Archived accounts cannot be renamed or reactivated.
- Direct Domain unit tests. Existing health/OpenAPI integration tests are unchanged.

This is domain behavior only. Account opening balances, opening dates, descriptive types, persistence, resource authorization, and all financial workflows remain deferred. The name limit is measured in UTF-16 code units, as detailed in the domain model.

## Iteration 3: implemented persistence and identity foundation

- Official stable EF Core/SQL Server/Identity packages at 10.0.15, targeting SQL Server 2019 compatibility 150.
- Infrastructure-owned Identity user and DbContext with Guid keys; Account mapping and ownership foreign key without domain changes.
- Multiple accounts and DOP/USD currencies per user, replacing the original single-currency proposal.
- Secure application-cookie and antiforgery preparation without authentication or financial routes.
- Initial migration, conservative local preflight/apply script, and opt-in real SQL tests with rollback-only synthetic data.
- No automatic startup migration, balance columns, financial workflows, or frontend.

## Proposed MVP: remaining work

Iteration 4 implements registration, cookie login/logout, current-user lookup, and CSRF bootstrap. Identity validates passwords and normalizes email, while the API enforces CSRF on all mutations and rate limits login/registration. No schema changes or financial endpoints were required for that iteration. Account and other financial use cases were still pending at that stage.

Iteration 5 implements authenticated account creation, owner-only listing/detail, rename, and idempotent archive. Inputs reject client ownership/status assignment; responses include currency/state and no balances. Lists have bounded pagination, stable name/Id ordering, and state filters. Every existing-account operation scopes by the server-authenticated owner; foreign/missing IDs both return 404. The existing domain/mapping/schema supports this without migration changes. Opening balances/dates, account types, monetary operations, and frontend remain deferred.

1. Personal identity: register, log in, and log out through ASP.NET Core Identity and secure cookie sessions.
2. Accounts: create and rename owned accounts, record an opening balance, and archive accounts without losing history.
3. Categories: manage owned income and expense categories and archive referenced categories.
4. Transactions: record, list and filter owned income/expense operations; correct through full compensating reversals, never editing/deleting confirmed history. Opening corrections atomically reverse and replace while preserving history.
5. Transfers: move money atomically between two active accounts owned by the same user, without classifying the movement as income or expense.
6. Budgets: set monthly spending limits per expense category and report actual spending against those limits.
7. Summary: expose basic balances, income, expense, and budget totals through the backend. A React and TypeScript dashboard will consume these endpoints in a later iteration.
8. Accountability: audit relevant changes and protect retryable financial write operations with idempotency.
9. Persistence: extend the implemented EF Core/SQL Server schema and database-backed integration tests as financial use cases arrive.

One user may own accounts in different currencies: DOP and USD. This supersedes the original single-reporting-currency proposal. Future transactions must use their account's currency; transfers require equal currencies. Summaries and budgets must keep currencies separate without implicit conversion. Money retains two effective decimal places and its signed decimal range. The accepted financial contract bounds positive operation magnitudes to decimal(28,2); future persistence must prove exact round trips.

## Accepted financial architecture and current increment

[ADR-002](decisions/ADR-002-financial-operation-journal.md) and the [financial engine design](financial-engine-design.md) govern the immutable journal and derived balances. No overdrafts, credit accounts, negative openings, user backdating or partial reversals. Recording uses UTC and America/Santo_Domingo accounting dates. Reversal is full and at most once per original; archived-account reversals require a controlled exception preserving nonnegative balances.

The current increment implements only bounded FinancialAmount, FinancialPostingContext, operation kind/status and immutable FinancialOperation factories with direct Domain tests. It adds no persistence, financial endpoints, balances or posting services. Explicit zero opening revisions, atomic opening correction, lifetime owner/type/key idempotency, audit, deterministic account locks and the single transactional writer remain future work. Domain construction alone does not establish their SQL guarantees.

## Quality requirements

- Domain remains independent of frameworks, HTTP, persistence, and identity implementation details.
- Each owned resource is authorized against the authenticated user; a valid identifier alone grants no access.
- Money uses decimal arithmetic with explicit currency and precision rules.
- Transfers, balance-affecting writes, idempotency records, and their audit records have defined transactional boundaries.
- Validate request shape at the HTTP boundary and business rules inside use cases/domain behavior.
- Tests verify invariants, authorization failures, persistence behavior, and retry/concurrency behavior as these features arrive.
- Source control contains no secrets, certificates, real connection strings, or personal finance data.

## Outside the MVP

Bank synchronization, payment execution, investment portfolios, loans, tax calculations, currency-converted consolidated reporting, exchange rates, cross-currency transfers, recurring transaction automation, shared accounts, organizations, social features, native mobile clients, and external notification integrations are excluded.

Microservices, RabbitMQ, Redis, MediatR, generic repositories, and speculative abstractions are not part of the initial architecture.

## Foundation acceptance

Restore, build, and tests must pass with the configured stable SDK. The API must return HTTP 200 and exactly the documented health payload, OpenAPI must be limited to Development, and production references must follow the dependency rules. Domain must remain independent of frameworks and other projects. Money and Account unit tests must pass without API dependencies. No unimplemented persistence or financial workflows may be claimed as complete.
