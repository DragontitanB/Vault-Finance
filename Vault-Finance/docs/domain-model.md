# Domain Model

## Status and conventions

Money, Account and the immutable financial Domain types retain their existing behavior. Infrastructure maps Account, Identity users and dedicated financial journal records; Domain entities gain no ORM dependencies, setters or persistence constructors. Application/API support owned account creation, listing, detail, rename, and archive. Initial opening, Income/Expense/Transfer, derived-balance reads and their idempotency/audit execute through the controlled SQL boundary. Category, Budget, Reversal/correction workflows and financial HTTP endpoints remain future work. See the [journal storage model](financial-journal-persistence.md).

Account uses opaque, caller-supplied, non-empty Guid identifiers for Id and UserId. The entity does not generate identifiers or verify that a User exists. Financial timestamps are UTC with America/Santo_Domingo accounting dates. The future trusted writer selects current timestamps; users cannot backdate operations in the MVP.

## User

Represents the owner of personal finance data. Infrastructure implements ApplicationUser : IdentityUser<Guid> with the standard Identity fields, EF stores, and registration/login/logout/current-user use cases. There is no separate domain User entity, reporting-currency field, or profile extension. Authentication HTTP responses expose only Id and Email. The domain ownership concept remains a Guid identifier and does not inherit from IdentityUser or contain password hashes.

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

The descriptive account type remains deferred. Opening values/dates now belong to journal batches/revisions through the separate opening use case, not mutable Account properties. There are no deposits, withdrawals, transfers, mutable balances, persistence constructors, or ORM dependencies.

The current derived-balance read uses complete committed journal batches, including openings, income, expenses, transfers and exact compensations. Negative balances and credit accounts are forbidden. Ordinary activity requires Active accounts; the controlled reversal exception for archived accounts must also preserve nonnegative balances. There is no persisted balance cache.

The earlier one-reporting-currency-per-user proposal is superseded: one user may own multiple accounts in different currencies, DOP and USD. The Accounts.UserId foreign key enforces owner existence in persistence, with deletion restricted while accounts exist. Account authorization is now enforced separately: the server supplies UserId from the authenticated identity, and every existing-resource query/update scopes by owner and Id. A foreign key alone is not authorization.

Account HTTP input exposes only Name/Currency for creation and Name for rename. Unknown JSON fields are rejected, including ownership, identity, and status fields. Archive accepts no body or an empty object. Responses expose only Id, Name, Currency, and Status, without owner data or invented balances. Accounts remain queryable after archiving; rename returns a conflict for an owned archived account. Foreign/missing identifiers both produce 404.

The existing Account class and EF mapping were sufficient: no setters, constructors, domain behavior, table columns, or migrations changed. The application invokes Account.Rename/Archive. Infrastructure applies a status-conditioned rename and an archive-only status update, so stale snapshots cannot rename after a committed archive or reactivate a row. Other concurrent active renames remain last-write-wins.

EF explicitly maps the existing get-only properties and binds the existing constructor. It then restores persisted Status through its private setter. Reloading an archived account preserves its state and prohibition on renaming. Domain has no EF references, public setters, or new persistence constructor. EF also rejects changes to persisted UserId/Currency through the change tracker.

Accounts stores Id/UserId as uniqueidentifier, Name as nvarchar(100), Currency as varchar(3), and Status as int. CHECK constraints reject empty identifiers, whitespace at either name boundary, empty names, unsupported/non-uppercase currencies, and unknown status values. Whitespace validation matches the current String.Trim whitespace set; internal whitespace remains valid. There are non-unique indexes on UserId and (UserId, Status), with no account-name uniqueness constraint.

## Transaction

Income and Expense are now kinds of the immutable FinancialOperation descriptor. Their future posting/reporting workflows and Category linkage are not implemented; no separate mutable Transaction entity is introduced.

Future Category linkage must match owner and income/expense kind. Operation currency matches the account currency; no single user-level currency is imposed. Posting requires an explicitly initialized account and a trusted current recording date.

Income increases the derived balance and expense decreases it. Confirmed operations cannot be edited or deleted; full compensations preserve originals and their historical dates. Transfers do not count as income or expense.

## Category

Classifies income or expenses for one user. Proposed information: Id, UserId, name, kind, and archive state.

The initial design proposes flat categories with names unique per owner and kind. Archived categories remain visible in history but cannot be assigned to new entries. Hierarchies and global shared category catalogs are outside the initial design.

## Budget

Represents a monthly expense limit for one owned expense category. Proposed information: Id, UserId, CategoryId, calendar month/year, and positive Money limit.

Allow at most one budget per user, category, month, and currency. Actual spending must derive only from expense transactions in the budget's currency; DOP and USD must not be added together. Transfers do not count as spending. The exact budget scope will be revisited before implementation. Rollover and carryover are deferred.

## Transfer

Represents one movement between distinct owned accounts. Proposed information: Id, UserId, source AccountId, destination AccountId, positive Money amount, effective date, optional description, and timestamps.

Both accounts must be active, initialized, distinct, belong to the same user, and use the same currency. The immutable Transfer descriptor stores both participants and one positive magnitude. The [controlled transfer writer](financial-transfers.md) posts it atomically, with equal opposite derived effects and no income/expense category. It locks both accounts, validates source sufficiency/destination headroom and final conservation. There are no independently writable transfer legs. Initialization and current funds are SQL writer guarantees, not facts proved by a descriptor factory.

## AuditEntry

Records a relevant action on a resource. Proposed information: Id, actor UserId where applicable, action, resource type and identifier, UTC timestamp, correlation identifier, and a minimal approved change summary.

Future financial audit is append-only, commits with the operation batch and idempotency outcome, and is retained for the MVP lifetime. Do not include passwords, cookies, tokens, or unrestricted request bodies. Account erasure, system actors, and administrator access need a separate lifecycle design; there is no MVP history-erasure workflow.

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

Currency validation is shared through an internal CurrencyCode helper, so Account and Money cannot diverge. This is not a public currency abstraction. Money is not a property of Account and is not mapped as an EF entity, separate table, or invented balance column. Its decimal range and arithmetic rules remain unchanged. FinancialAmount deliberately narrows positive posting magnitudes to the decimal(28,2) range; a generic full-range Money must not silently be mapped to that narrower column.

## Financial operation primitives: implemented Domain only

[ADR-002](decisions/ADR-002-financial-operation-journal.md) accepts an immutable operation journal with derived balances. See the [complete contracts and SQL obligations](financial-engine-design.md).

- FinancialAmount is a sealed value record wrapping unchanged Money. It requires 0 < Amount <= 99999999999999999999999999.99, two effective decimal places, DOP/USD and value equality.
- FinancialPostingContext is a sealed value record with a non-empty BatchId, a non-default zero-offset DateTimeOffset, and an effective date derived in America/Santo_Domingo. It does not read the clock or prove that a supplied instant is current.
- FinancialOperationKind defines Opening, Income, Expense, Transfer and Reversal. FinancialOperationStatus defines Confirmed only.
- FinancialOperation is a sealed immutable entity with validated factories, non-empty Id/UserId, account IDs, currency, recording context and a trimmed reason of at most 200 UTF-16 code units. Ordinary operations carry a FinancialAmount and use Active, owned, matching-currency account snapshots.
- Opening credits its destination; Income credits its destination; Expense debits its source; Transfer has distinct source/destination IDs. Opening requires a reason.
- Reversal references the original ID/kind with no copied amount/account fields. It requires the same owner, a reason, distinct operation/batch IDs and a timestamp not before the original. Reversing a reversal is forbidden. Creating a descriptor does not mutate its original or prove that another reversal was not already posted.
- There are no setters, init setters, public entity constructors, or mutable confirmation/reversal transitions. Confirmed describes the immutable representation; SQL commit is a separate future guarantee.

Zero initialization is future audited opening-version metadata, not a zero monetary operation. Opening correction will append a full reversal and positive replacement (when needed) in one atomic batch and advance the opening revision.

Infrastructure stores opening versions, operations, batches, outcomes and audit, with typed FKs, scoped uniqueness and append-only guards. There is no additional Domain opening-version entity or mutable balance. Application/Infrastructure implement initial opening, canonical replay and locked derived reads with complete-batch validation. Income/Expense/Transfer use exact locked funds/headroom checks with initialized/Active owned accounts. Reversal/correction coordination and financial HTTP endpoints remain future work. See the [opening contract](financial-opening-writer.md) and [transfer protocol](financial-transfers.md).

```csharp
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.ValueObjects;

var ownerId = Guid.NewGuid();
var cash = new Account(Guid.NewGuid(), ownerId, "Cash", "DOP");
var posting = new FinancialPostingContext(
    Guid.NewGuid(), new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero));
var income = FinancialOperation.CreateIncome(
    Guid.NewGuid(), ownerId, cash, new FinancialAmount(new Money(100m, "DOP")), posting);
var compensation = FinancialOperation.CreateReversal(
    Guid.NewGuid(), ownerId, income,
    new FinancialPostingContext(Guid.NewGuid(), posting.RecordedAtUtc.AddMinutes(1)),
    "Correct a simulated entry");
// Immutable descriptors only: neither construction moves funds or persists data.
```

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

Account and financial journal mappings, constraints and bounded decimal/date round trips are tested on a dedicated SQL database. InitialPersistence and Domain are unchanged. Opening, Income/Expense/Transfer, account/key locks, balances and scoped replay are verified under ADR-002, including transfer conservation and both archive orders. Reversal/correction execution remains future work; see financial-transfers.md. Domain has no framework/project dependencies. Descriptor checks do not replace authorized owner context or locked SQL revalidation.
