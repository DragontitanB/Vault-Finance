# Domain Model

## Status and conventions

Money and the initial Account entity are implemented in Domain. User, Transaction, Category, Budget, Transfer, AuditEntry, and all persistence relationships remain future proposals. There are no tables, mappings, application use cases, or financial endpoints yet.

Account uses opaque, caller-supplied, non-empty Guid identifiers for Id and UserId. The entity does not generate identifiers or verify that a User exists. Proposed future event timestamps use UTC; transaction dates and budget months represent user-facing calendar concepts and need an explicit timezone policy before implementation.

## User

Represents the owner of personal finance data. Proposed information: Id, reporting currency, display preferences, and creation time. ASP.NET Core Identity will manage credentials and sessions through Infrastructure. The domain ownership concept will not inherit from IdentityUser or contain password hashes.

One User owns many Accounts, Categories, Transactions, Budgets, and Transfers. Referenced ownership must agree for every operation. Account closure and user/data deletion policies need a separate lifecycle decision.

## Account

Implemented as a sealed entity in `VaultFinance.Domain.Accounts`. Its current constructor is `Account(Guid id, Guid userId, string name, string currency)`.

- Id and UserId must not be Guid.Empty. Id, UserId, and Currency are read-only after construction. There is no ownership reassignment or currency-change operation.
- Name is required, trimmed, and limited to 1 through 100 UTF-16 code units after trimming. Internal whitespace is preserved. Account.MaximumNameLength exposes the limit. Name uniqueness is not imposed without a concrete use case.
- Currency uses the same DOP/USD normalization and validation rules as Money.
- AccountStatus defines Active = 1 and Archived = 2. Every new account is Active; callers cannot choose or assign its status.
- Rename validates the new name before assignment and is allowed only for Active accounts. A failed rename leaves the existing account unchanged. Even renaming an archived account to its current name is rejected.
- Archive changes Active to Archived while preserving the remaining data. Archiving an already archived account is an idempotent no-op. There is no reactivation operation.
- Invalid creation or business changes throw DomainException with an explicit message.

The earlier proposal included a descriptive type, opening balance, and opening date. These remain deferred to respect the current scope. There are no deposits, withdrawals, transfers, mutable balances, persistence constructors, or ORM dependencies.

The future balance will be derived from an opening balance, income, expenses, and transfer legs. Negative balances may be valid; overdraft rules are not assumed. Future writes must respect the archive state. Do not introduce a persisted balance cache until a demonstrated need and consistency strategy exist.

The proposed one-reporting-currency-per-user policy remains a future application rule. Account cannot enforce it without user configuration or a use case; this iteration guarantees only a supported, immutable currency per account. Owner existence and resource authorization are also deferred.

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

Implemented as a sealed record in `VaultFinance.Domain.ValueObjects`, constructed with `Money(decimal amount, string currency)`. Amount and Currency have getters only. A reference type avoids the invalid default instance that an unrestricted struct could expose. There is no independent identity, binary floating-point amount, public setter, or init setter.

- Only ISO 4217 codes DOP and USD are supported. Leading/trailing whitespace is removed; three ASCII letters are required and normalized using invariant uppercase. Empty, null, malformed, Unicode lookalike, and unsupported codes are rejected. There is no currency registry lookup.
- Both supported currencies use at most two effective decimal places. For example, 1.2300 is valid because trailing zeros add no monetary precision; 1.001 is invalid. Input is rejected rather than rounded.
- Zero and negative values are valid. Future transaction/transfer magnitudes and budget limits must enforce positivity in their own behavior.
- Equality, ==, !=, and hash codes use the decimal amount and normalized currency. Equal amounts in different currencies are unequal, without performing conversion. Decimal trailing-zero scale does not affect equality.
- Add/Subtract and the + / - operators require matching currencies, return a new Money, and leave both operands unchanged. Results may be negative or zero.
- Invalid currency, excessive precision, incompatible currency, decimal overflow, or a calculation that would silently lose precision throws DomainException. Overflow retains the original OverflowException as its inner exception. Null arithmetic operands are programming errors and throw ArgumentNullException.
- No arbitrary business maximum is imposed beyond decimal representability. Near its numeric limits decimal arithmetic can round without throwing; inverse-operation checks reject that loss of exactness. A representable input does not guarantee that every calculation involving it is representable.
- Ordering, multiplication/division, exchange rates, currency conversion, formatting policy, and serialization contracts are not implemented.

Currency validation is shared through an internal CurrencyCode helper, so Account and Money cannot diverge. This is not a public currency abstraction. SQL precision and persistence limits still need a decision when persistence is introduced; no future SQL schema is implied by the current decimal range.

## Usage examples

```csharp
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.ValueObjects;

var original = new Money(100.25m, " usd ");
var total = original + new Money(20m, "USD"); // 120.25 USD
var difference = original.Subtract(new Money(150m, "USD")); // -49.75 USD
var equivalent = new Money(100.2500m, "USD");
bool equal = original == equivalent; // true

var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "  Cash  ", "dop");
account.Rename("Emergency fund");
account.Archive();
account.Archive(); // Idempotent; still Archived.
```

Adding `new Money(1m, "DOP")` to `original` throws DomainException. Creating `new Money(1.001m, "USD")` also throws. Calling `account.Rename(...)` after archiving throws and preserves the name. These examples are domain behavior only; no records are persisted and no funds are moved.

## Persistence and concurrency decisions still required

EF mappings, SQL precision, indexes, database constraints, deletion behavior, optimistic concurrency, balance query strategy, and idempotency storage will be designed with concrete use cases. Ownership and core invariants need application checks plus appropriate persistence constraints. The implemented Money and Account types have no framework or project dependencies. There are no repositories, DbContext, migrations, financial workflows, or persisted balances.
