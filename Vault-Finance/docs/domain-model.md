# Proposed Domain Model

## Status and conventions

This document is a future design proposal. None of these entities, value objects, relationships, constraints, or tables are implemented. Field names are conceptual rather than a committed persistence schema.

Proposed identifiers are opaque and stable. Owned resources carry a UserId. Timestamps representing events use UTC; transaction dates and budget months represent user-facing calendar concepts and need an explicit timezone policy before implementation.

## User

Represents the owner of personal finance data. Proposed information: Id, reporting currency, display preferences, and creation time. ASP.NET Core Identity will manage credentials and sessions through Infrastructure. The domain ownership concept will not inherit from IdentityUser or contain password hashes.

One User owns many Accounts, Categories, Transactions, Budgets, and Transfers. Referenced ownership must agree for every operation. Account closure and user/data deletion policies need a separate lifecycle decision.

## Account

Represents a financial container such as a cash wallet or bank account. Proposed information: Id, UserId, name, descriptive type, currency, opening Money balance, opening date, and archive state.

The balance will be derived from the opening balance, income, expenses, and transfer legs. Negative balances may be valid; overdraft rules are not assumed. Archiving preserves history and prevents new entries. Do not introduce a persisted balance cache until a demonstrated need and consistency strategy exist.

## Transaction

Represents an income or expense entry. Proposed information: Id, UserId, AccountId, CategoryId, kind, positive Money magnitude, effective date, optional description, and creation/update timestamps.

Account and Category must belong to the same User. Category kind must match transaction kind. Money currency must match the account and user's reporting currency. A transaction predates neither its account's opening date nor other lifecycle constraints established later.

Income increases the account balance and expense decreases it. Changes and deletions will update derived totals and produce an audit record. Transfers are modeled separately to prevent counting them as income or expense.

## Category

Classifies income or expenses for one user. Proposed information: Id, UserId, name, kind, and archive state.

The initial design proposes flat categories with names unique per owner and kind. Archived categories remain visible in history but cannot be assigned to new entries. Hierarchies and global shared category catalogs are outside the initial design.

## Budget

Represents a monthly expense limit for one owned expense category. Proposed information: Id, UserId, CategoryId, calendar month/year, and positive Money limit.

Allow at most one budget per user, category, and month. Actual spending derives from matching expense transactions across that user's accounts. Transfers do not count as spending. Rollover and carryover are deferred.

## Transfer

Represents one movement between distinct owned accounts. Proposed information: Id, UserId, source AccountId, destination AccountId, positive Money amount, effective date, optional description, and timestamps.

Both accounts must be active, belong to the same user, and use the same currency. The source decreases and destination increases by the same amount in one database transaction. A transfer has no income/expense category and must not be counted in spending reports. Whether persistence stores one transfer row or linked ledger legs remains a later decision.

## AuditEntry

Records a relevant action on a resource. Proposed information: Id, actor UserId where applicable, action, resource type and identifier, UTC timestamp, correlation identifier, and a minimal approved change summary.

Audit data is append-only through application behavior. Financial writes and their audit entries should commit atomically. Do not include passwords, cookies, tokens, or unrestricted request bodies. Retention, account erasure, system actors, and administrator access require an explicit policy before implementation.

## Money

An immutable value object combining a decimal Amount and explicit currency code. It has no independent identity and no binary floating-point representation.

Addition, subtraction, and comparison require equal currencies. Account balances may be signed; transaction/transfer magnitudes and budget limits must be positive. Each use case enforces its own sign rules rather than making every Money value positive.

Supported currencies, permitted scale, maximum magnitude, and SQL precision must be agreed before implementation. The proposed input policy rejects unsupported excess precision instead of silently rounding. Currency conversion is excluded.

## Persistence and concurrency decisions still required

EF mappings, indexes, database constraints, deletion behavior, optimistic concurrency, balance query strategy, and idempotency storage will be designed with concrete use cases. Ownership and core invariants need application checks plus appropriate persistence constraints. No entity classes, repositories, DbContext, migrations, or financial algorithms are part of this iteration.
