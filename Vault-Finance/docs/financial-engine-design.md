# Financial Engine Design

Status: Proposed; documentation only. Date: 2026-11-23.

This is a design for simulated personal finances, not a banking, custody, payment, or settlement system. A recorded balance is a calculation over user-entered simulation data, not evidence of funds at a financial institution.

No financial entities, services, endpoints, packages, database changes, or migrations are implemented by this document. [ADR-002](decisions/ADR-002-financial-operation-journal.md) records the recommendation. Product decisions Q1-Q8 below must be resolved before implementation.

## 1. Baseline inspected and conflicts to resolve

The repository currently contains:

- Immutable Money using decimal, DOP/USD, two effective decimal places, signed/zero values, value equality, and checks for overflow or loss of arithmetic exactness. Its tests explicitly accept decimal.MaxValue and decimal.MinValue.
- Account with immutable Guid identity/owner/currency, validated name, Active/Archived state, Rename, and idempotent Archive. There is no balance, opening date, account type, reactivation, or creation timestamp.
- Owner-scoped account use cases and an EF adapter. Reads use AsNoTracking; rename uses a conditional update to prevent editing after archive. No financial posting protocol exists.
- Identity cookies, CSRF, ProblemDetails, and SQL Server 2019 compatibility 150. InitialPersistence maps Accounts and Identity only.
- Existing sequential HTTP/SQL tests sharing a transaction per test. The repository reports 181 passing cases; this design task does not rerun or independently certify that baseline.

Existing future proposals are not all compatible with an immutable engine:

| Existing proposal | Proposed resolution, subject to approval |
| --- | --- |
| Correct/delete transactions in requirements and domain-model | Confirmed operations cannot be edited/deleted. Corrections use compensating operations; drafts are outside the journal. |
| No arbitrary Money maximum; preserve its decimal range in persistence | Keep Money unchanged. Either approve an explicit posting/balance range, or implement and prove a full-range representation. Ordinary decimal(18,2) is insufficient. |
| Negative balances may be valid; overdraft rules undecided | Recommend funded simulated accounts with no negative balance for the first engine. Liability/credit accounts require a separate policy. |
| Archived accounts block new activity, but correction behavior unspecified | Block ordinary postings; propose a narrow exception for exact compensating reversals, without reactivation. |
| Category on future transactions | Categories are not implemented. Do not invent a required category FK/table in this first engine; add classification in a later increment. |
| Future idempotency keys scoped by owner/operation | Propose one owner/key namespace across financial command kinds, with kind included in the fingerprint. This scope change also needs agreement. |

The existing Domain, requirements, and architecture are not silently rewritten here. Approval must reconcile the affected future requirements before coding.

## 2. Two persistence models

### A. Full double-entry ledger

Persist a JournalEntry and two or more immutable Posting rows against ledger accounts. For every committed entry and currency, total debits must equal total credits.

User-visible accounts are asset-like ledger accounts; external income, expenses, and opening principal need counterpart accounts. For example, simulated income debits a user account and credits an income counter-account; a transfer debits the destination and credits the source.

This supports a complete accounting equation, richer liabilities/equity, split entries, and accounting-oriented reporting. It also requires ledger-account semantics, counterpart management, posting rules, and enforcement of cross-row balancing. A simple row CHECK cannot establish a sum over multiple postings. A controlled transaction/write boundary must enforce it.

Double entry does not itself prevent an asset balance from becoming negative, provide idempotency, authorize ownership, or make unbalanced partial writes impossible. Those still need explicit controls.

### B. Immutable operation journal with derived balances

Persist one canonical FinancialOperation per confirmed operation. An ordinary row stores a magnitude and source/destination references. Income has only a destination; expense only a source; transfer has both and one shared amount.

Derive one or two signed account movements from that row through a read-only projection. A reversal references an immutable original and derives its exact opposite; it does not store editable copies of its amount or accounts.

This is an auditable operation journal, not full double-entry accounting. Income/expense are external boundaries without counterpart ledger accounts.

| Criterion | A: double entry | B: canonical operation journal |
| --- | --- | --- |
| Authoritative data | Entry plus balanced posting set | One operation row; movements derived |
| Income/expense | Requires counterpart accounts | One actual account effect |
| Internal transfer | Two balanced postings, grouped atomically | Both accounts and common amount in one row |
| Global accounting equation | Supported if posting rules are enforced | Not a full general ledger |
| Audit/reversal | Immutable entry and compensating entry | Immutable operation and exact inverse reference |
| Balance consistency | Aggregate postings; cache optional | Aggregate derived movements; cache unnecessary initially |
| Overspending/idempotency | Additional concurrency/key protocol needed | Same additional protocol needed |
| Complexity for current scope | Ledger accounts, types, balancing rules | Five bounded operation kinds and a read projection |
| Natural expansion | Liabilities, split entries, full accounting | Personal income/expense and same-owner transfers |
| Principal limitation | More semantics and failure surfaces now | Later GL capabilities require explicit migration |

### Recommendation

Choose B for the MVP. It meets the current personal-finance scope without artificial income/equity accounts, event sourcing, microservices, messaging, generic repositories, or balance caches.

The design intentionally avoids a pair of independent transfer transaction rows and a second persistent movements table. One canonical row makes a half-transfer structurally unrepresentable. Its two movements are derived, not independently writable.

Reconsider A when liabilities, split allocations, full accounting statements, or external accounting integration become actual requirements. Do not advertise B as double entry merely because transfers have two effects.

## 3. Proposed domain and lifecycle

These are conceptual types, not new code:

- FinancialOperation: immutable confirmed aggregate identified by Guid; owner, currency, operation kind, effective date, recording instant, reason, and source/destination or reversal reference.
- OpeningBalance: initializes one account explicitly, including zero. Its date establishes the account's financial baseline.
- Income: positive magnitude credited to one owned account.
- Expense: positive magnitude debited from one owned account.
- Transfer: positive magnitude moved between distinct owned accounts with equal currency.
- Reversal: a reference to one owned, non-reversal operation; reverses all of it exactly once. No client-selected amount/accounts.
- AccountMovement: read projection identified by operation and source/destination leg, containing signed Money and dates; not a separately persisted aggregate.
- FinancialRequestOutcome: durable success/rejection receipt for an idempotency key.
- FinancialAuditEntry: minimal immutable evidence linking an outcome to the actor, event/reason, and correlation identifier.

Only committed operations exist in the journal. Drafts, scheduled movements, reservations, holds, and bank-available balances are excluded. Do not introduce Pending/Confirmed state transitions before a real need exists.

Application will own posting/reversal use cases and narrow persistence/clock ports. Domain will own pure shape, amount, currency, and reversal rules. Infrastructure will own SQL locks, transactions, mapping, aggregation, and durable outcomes. API will own authenticated DTOs, CSRF, headers, validation, and ProblemDetails. No existing Account balance setter is required.

## 4. Proposed business invariants

1. Owner identity comes from authenticated server claims, never the request body.
2. All referenced accounts, original operations, and outcomes belong to that owner. A missing or foreign identifier produces the same 404.
3. DOP/USD remain separate. Every operation uses the participating account currency; both transfer accounts must match. No FX or mixed-currency total is inferred.
4. Income, expense, and transfer magnitudes are strictly positive. Opening may be zero. Signed movement/balance values do not change Money's generic sign semantics.
5. Each account requires one explicit opening record before ordinary financial activity. An uninitialized balance is unavailable, not silently zero.
6. A transfer cannot target its own source account and cannot create two independently committed effects.
7. Under the proposed funded-account policy, every affected account's resulting balance must remain within [0, Bmax]. Validate destination overflow as well as source sufficiency.
8. Confirmed operation fields cannot be updated/deleted; reversals retain the original record and reason.
9. At most one full reversal exists per original. Reversal of a reversal and partial reversals are excluded.
10. Opening principal is not income; transfers are neither income nor expense. Income reversals reduce income; expense reversals reduce expense.
11. All ordinary participating accounts must be Active while the operation commits. Status checked before a transaction is insufficient.
12. Archived accounts remain visible in balance/history queries. Archiving never removes their balance or sets it to zero.
13. Operation, durable request outcome, and financial audit evidence commit together, or no operation commits.
14. Repeated matching idempotency keys return the first durable outcome; they do not create another operation.
15. Financial posting order, dates, and numeric checks follow the same rules for reversals as for original operations.

### Opening, corrections, and archived accounts

Recommend explicit initialization for existing and new accounts, including a zero opening. The current account create contract remains name/currency only. A future opening use case can initialize it without changing Account or inventing a mutable OpeningBalance property.

An opening record is one-time per account. Reversing it cancels its contribution but does not erase initialization or its baseline date. For the minimal proposal, a second opening/replacement opening is not supported. Correcting an opening to a different amount needs an explicit approved adjustment/replacement design before enabling that feature; do not disguise it as income. This is decision Q5, not a solved workflow.

Recommend allowing exact reversals on archived accounts as a narrow audit exception. The caller supplies only the original operation reference and reason. Ownership, currency, one-reversal, sufficiency, and upper-bound checks still apply. The account stays Archived. This provides a correction path without adding reactivation or allowing arbitrary new income/expense/transfer activity on archived accounts.

Reversing an income or incoming transfer can be rejected if the received amount has already been spent. Never force a negative balance to make a reversal succeed. Reversing dependent spending first may restore eligibility. No automatic cascade or forced admin adjustment is proposed.

For income/expense/transfer corrections, a reversal and a replacement are individually atomic commands. Two HTTP calls are not an atomic correction bundle; an all-or-nothing replacement workflow requires a later explicit command.

## 5. Numeric storage: a decision gate

Current Money accepts the CLR decimal range, not just the range of a typical SQL monetary column. Changing that type is outside this design task.

| Representation | Assessment |
| --- | --- |
| SQL money/float/real | Reject: wrong scale contract or approximate arithmetic. |
| decimal(18,2) | Only 16 integer digits; silently adopting it would contradict existing Money boundary tests. |
| decimal(31,2) with CLR maximum CHECK | SQL can hold the numeric maximum plus two fractional places, but that alone does not establish safe SqlClient/CLR round-trip behavior. |
| decimal(28,2) with an explicit posting bound | Predictable two-place CLR representation across its whole column range; narrows the set of accepted financial commands and requires approval. |
| Full-range minor units or canonical representation | Possible with SqlDecimal/BigInteger or another exact conversion boundary; more mapping/query complexity, to be proven if required. |

Provisional MVP recommendation: monetary columns decimal(28,2), with Bmax = 99999999999999999999999999.99, and explicit validation of both amounts and resulting per-account balances. This is a financial-use-case limit, not a change to Money. A mathematically valid Money outside this range must be rejected before SQL, with a documented error, never truncated or rounded. If preserving the complete existing Money range in persistence is mandatory, reject this recommendation and resolve the full-range mapping first.

A read-only diagnostic in Windows PowerShell 5.1 / .NET Framework 4.8 parsed 79228162514264337593543950335.00 as SqlDecimal with precision 31/scale 2; get_Value threw OverflowException. The decimal(28,2) upper bound converted exactly. This was not a .NET 10/EF/SQL Server round-trip test. Large fixed-scale coefficients can exceed CLR decimal storage despite fitting numerically at a lower scale. The [.NET implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Data.Common/src/System/Data/SQLTypes/SQLDecimal.cs) demonstrates that conversion limit. Current [SqlClient source](https://github.com/dotnet/SqlClient/blob/main/src/Microsoft.Data.SqlClient/src/Microsoft/Data/SqlClient/SqlBuffer.cs) also contains trailing-zero normalization: the BCL failure is not proof that the installed provider fails. Pin the provider version and test its real boundary behavior before deciding.

SQL SUM over decimal uses decimal(38, scale). Keep wide aggregation and range comparisons in SQL; only convert a validated, bounded per-account result to CLR decimal. Do not cast an arbitrary global aggregate directly into Money. Consolidated totals beyond that contract are deferred. See [SQL SUM return types](https://learn.microsoft.com/en-us/sql/t-sql/functions/sum-transact-sql?view=sql-server-ver15) and [precision/scale rules](https://learn.microsoft.com/en-us/sql/t-sql/data-types/precision-scale-and-length-transact-sql?view=sql-server-ver15).

A wide type is not automatically a safe arithmetic intermediate. Adding two declared decimal(38,2) values requests precision 39; SQL's result-type rules can reduce the scale to one even for tiny values. Do not compute balance-plus-amount or reconciliation residuals using unchecked wide decimal arithmetic. First range-check the aggregate, then cast the bounded account balance to decimal(28,2). Adding/subtracting a decimal(28,2) magnitude then yields a decimal(29,2) intermediate; validate its result against [0, Bmax] before narrowing.

For global reconciliation, compare corresponding exact sums directly, or use explicitly derived integer minor units and arbitrary-precision accumulation. Never accept a rounded residual as zero. Test the actual SQL expression metadata and one-cent cases, not just column definitions.

Validate effective precision before binding a fixed-scale SQL parameter: SQL conversion can round. Input containing 1.001 must not become 1.00. Trailing zeros such as 1.2300 remain numerically compatible with current Money.

Recommend decimal strings at future HTTP boundaries to avoid a JavaScript binary-number round trip. Define invariant parsing with no grouping/exponent coercion, bounded input length, rejection of meaningful excess digits, and canonical two-place receipts/fingerprints. Very long fractional strings must not round to zero during parsing.

Future zero- or three-decimal currencies require an explicit Money policy, currency validation, schema, contract, and test revision. A wider SQL scale cannot by itself make current Money support them. Do not reinterpret already recorded amounts when extending currency support.

## 6. Dates and ordering

Store effective time separately from recording metadata:

- EffectiveDate: SQL date / DateOnly, used for financial-period reports.
- RecordedAtUtc: SQL datetimeoffset(7), restricted to offset +00:00, supplied by a trusted server clock; never client-assigned.
- AccountingTimeZoneId: the configured accounting calendar identifier captured with the operation.
- PostingSequence: bigint identity, generated after account locks; deterministic tie-breaker, not a gapless or global commit sequence.

Capture one trusted UTC instant from SQL Server after account locks are acquired, then derive the effective accounting date and recording metadata from that instant. Do not sample unrelated client/application clocks for different pieces of one operation. UTC still does not imply a perfectly monotonic physical clock.

Recommend a single explicitly configured accounting timezone initially; UTC is the provisional option, not an inferred user location. Opening can have a selected date no later than today in that calendar. Ordinary postings and reversals use the server-resolved current accounting date. Reject scheduling and backdating in the initial engine.

Under account locks, also reject a date earlier than the latest effective date affecting any participant, including a clock/configuration regression. This keeps per-account effective dates nondecreasing. Same-date history uses PostingSequence for a stable order.

If backdating is later allowed, checking today's balance is not sufficient to guarantee historical nonnegative balances. For example, income 100 on day 10 followed by an expense 80 backdated to day 5 leaves today's balance 20 but day 5 negative. Supporting that policy requires validating affected running-balance prefixes/suffixes under the same locks, or explicitly permitting negative historical balances.

Reversals take today's effective date; they do not rewrite the original reporting period. Historical queries include originals and reversals according to each row's own effective date, not by globally hiding reversed originals.

RecordedAtUtc is not a guaranteed commit timestamp. Identity sequences can have gaps, and transactions on unrelated accounts can commit out of sequence. Never use MAX(PostingSequence) alone as proof that all lower sequences have committed.

## 7. Proposed relational schema

No DDL or migration is created here. All new financial FKs use NO ACTION; no cascade deletion of history.

### Existing Accounts: proposed future candidate key only

Retain current columns, types, invariants, and domain API. Add a future unique candidate key (UserId, Currency, Id) to support ownership/currency composite FKs. Currency columns must share the existing column collation/type; retain binary-sensitive code CHECKs without casually changing the current column collation.

### FinancialOperations

| Column | Proposed type and purpose |
| --- | --- |
| Id | uniqueidentifier, server-generated immutable public PK |
| PostingSequence | bigint identity, unique clustered ordering key |
| UserId | uniqueidentifier, FK to Identity owner |
| Currency | varchar(3), required DOP/USD |
| Kind | tinyint: Opening, Income, Expense, Transfer, Reversal |
| Amount | decimal(28,2) under Q1; null only for Reversal |
| SourceAccountId | nullable uniqueidentifier, debit participant |
| DestinationAccountId | nullable uniqueidentifier, credit participant |
| OriginalOperationId | nullable uniqueidentifier, reversal reference |
| OriginalOperationKind | nullable tinyint, constrained non-reversal discriminator |
| EffectiveDate | date, required |
| RecordedAtUtc | datetimeoffset(7), required offset zero |
| AccountingTimeZoneId | bounded varchar, trusted configuration value |
| Reason | bounded nvarchar(200), immutable; required for opening/reversal |

Shape constraints must use explicit IS NULL/IS NOT NULL predicates; SQL CHECK accepts UNKNOWN and is not a substitute for nullability checks.

| Kind | Amount and account shape |
| --- | --- |
| Opening | Amount >= 0; destination only; no original reference |
| Income | Amount > 0; destination only; no original reference |
| Expense | Amount > 0; source only; no original reference |
| Transfer | Amount > 0; both accounts present and distinct; no original reference |
| Reversal | Amount/source/destination null; original ID/kind required; kind restricted to Opening/Income/Expense/Transfer |

Required keys/constraints:

- PK Id; unique PostingSequence.
- Candidate key (UserId, Currency, Id, Kind).
- Candidate key (UserId, Id) for same-owner outcome references. These additional unique keys are intentional SQL FK targets; uniqueness of Id alone does not declare a composite FK target.
- Composite source and destination FKs (UserId, Currency, AccountId) to Accounts(UserId, Currency, Id). These enforce same owner/currency for every non-null participant.
- Composite self-FK (UserId, Currency, OriginalOperationId, OriginalOperationKind) to the operation candidate key. The discriminator restriction prevents reversal-of-reversal without a cross-row CHECK.
- Filtered unique (UserId, DestinationAccountId) for Opening: one initialization record, including zero.
- Filtered unique (UserId, OriginalOperationId) where the reference is non-null: at most one full reversal.
- Required valid kind/currency/range/date-offset checks. State-sensitive rules such as current Active status and current funds require the posting protocol, not a row CHECK.

Index the source and destination access paths separately using owner/account/effective date/sequence, with needed amount/kind/reference fields included. Index reversals for parent lookup. Do not include large notes/receipts in every index.

### FinancialRequestOutcomes

| Column | Proposed type and purpose |
| --- | --- |
| Id, UserId | uniqueidentifier keys; server ownership |
| IdempotencyKey | varchar(128), bounded opaque ASCII key, binary case-sensitive semantics |
| FingerprintVersion, RequestHash | smallint and binary(32), versioned canonical intent hash |
| Outcome | Posted or Rejected; no durable Processing state |
| OutcomeCode | bounded stable application code |
| OperationId | nullable FK to an operation of the same owner |
| ReceiptVersion, ReceiptJson | bounded versioned semantic receipt; JSON check |
| RecordedAtUtc | datetimeoffset(7), offset zero |

Unique (UserId, IdempotencyKey) across financial command kinds; the command kind is part of the fingerprint. A filtered unique operation link prevents multiple successful outcomes claiming the same operation. Posted requires OperationId; Rejected requires it to be null.

Declare a candidate key (UserId, Id) for audit references and a same-owner FK (UserId, OperationId) to FinancialOperations(UserId, Id).

Store stable result fields, not cookies, CSRF tokens, passwords, stack traces, balances that later change, or request-specific trace headers. The JSON snapshot has an explicit small size limit. All outcomes are final and append-only.

### FinancialAuditEntries

Id, UserId, RequestOutcomeId, EventCode, RecordedAtUtc, ActorUserId, and a bounded server correlation identifier. For the initial owner-only engine, actor and owner must agree. The outcome links to any operation, avoiding a redundant second operation reference.

Unique (UserId, RequestOutcomeId) yields one financial audit event per newly finalized command outcome. A retry returns the existing outcome and does not append another financial event. Optional operational replay metrics are separate and contain no monetary payloads.

### Read-only AccountMovements view/projection

Normalize every ordinary row into its source/destination/amount. Normalize a reversal by joining its immutable original and swapping source/destination; use the original amount. Then emit a negative source movement and a positive destination movement.

The derived row identity is (OperationId, Leg). Carry owner, account, currency, effective date, recording instant, and sequence. A transfer always derives two equal/opposite effects from the same committed row. A reversal always derives the exact inverse; it cannot supply a different amount or participant.

No independently writable movements or balances table exists.

## 8. Guarantees and their boundaries

| Invariant | Mechanism | Limit |
| --- | --- | --- |
| No half-transfer | One canonical row and fixed read projection | Projection correctness needs tests/reconciliation |
| Exact reversal | Immutable parent reference and swapped derived effects | Privileged mutation of history invalidates the premise |
| Same owner/currency | Composite FKs plus authenticated lookups | FKs do not authenticate a caller |
| One initialization/full reversal | Filtered unique indexes | Opening replacement is intentionally not solved |
| No overspending/overflow | Account lock protocol and bounded result checks | Every posting writer must use the protocol |
| One result per key | Transaction-owned key lock, unique index, durable outcome | SQL-only scope; not external exactly-once delivery |
| Operation/outcome/audit atomicity | One explicit SQL transaction | FKs alone do not require every parent to have an audit child |
| Append-only history | No update/delete commands; dedicated runtime permissions | Does not resist a sysadmin or compromised migration identity |

The runtime role should have SELECT/INSERT, and no UPDATE/DELETE on new financial tables. Existing Identity/account metadata permissions remain separate. Do not run the deployed application as schema owner/sysadmin.

The local development login's actual role membership was not inspected in this task. Existing successful SQL tests are not evidence of least-privilege runtime enforcement.

The recommendation assumes one trusted Infrastructure posting boundary. INSERT permission alone cannot force a writer to acquire locks, check funds, or create all audit children. If untrusted writers or independent services must insert, use a permission-hardened posting stored procedure or another enforceable database boundary before exposing that capability. Do not claim that application conventions protect against arbitrary direct SQL.

## 9. Atomic posting and idempotency protocol

Use one SQL connection and explicit READ COMMITTED transaction for the complete financial command, not merely the final SaveChanges. No network calls, user interaction, or logging of request bodies while holding locks.

1. Authenticate, require CSRF, validate DTO shape/key/precision/limits, and canonicalize the client intent.
2. Begin the SQL transaction.
3. Acquire a transaction-owned exclusive sp_getapplock resource derived from owner and idempotency key, with a bounded timeout. Check its return code; a timeout/cancellation/deadlock is not successful acquisition.
4. Look up the durable outcome by owner/key. Matching hash/version returns the original semantic receipt without rechecking today's funds/archive state. A different intent returns conflict.
5. Resolve the owned participants and, for reversal, its immutable original. Acquire all account anchors in one deterministic order, then re-read owner/currency/status.
6. Read initialization/current balances in a new SQL statement after the locks have been acquired. Validate the entire operation's resulting balances and dates.
7. On a business rejection, insert only a final rejected outcome and audit event and commit. On success, insert operation, posted outcome, and audit event and commit together.
8. Send the result only after confirmed commit. An error after commit cannot erase the durable result.

The diagram shows the new successful-command path; replay and rejection exits follow the protocol above.

```mermaid
flowchart LR
    A["Authenticated command + key"] --> B["Begin SQL transaction"]
    B --> C["Lock owner/key; replay or continue"]
    C --> D["Lock owned accounts in order"]
    D --> E["Fresh balance/date/status checks"]
    E --> F["Append operation + outcome + audit"]
    F --> G["Commit before response"]
    G -. "read-only projection" .-> H["Account movements and derived balances"]
```

No durable Processing row/lease is needed: the native transaction-owned key lock serializes concurrent requests, and rollback/crash releases it. The unique key remains a final database defense. Resource names are namespaced, bounded below SQL's resource limit, and use a fixed hash; hash collisions may over-serialize but must never authorize or merge two distinct persisted keys.

A same-key retry may need a fresh CSRF token/session. Authorization and CSRF are always checked before replay. Replaying a first receipt after the account was subsequently archived is correct: the receipt describes the original posting, not current balance or current reversal status.

Business rejection receipts, including insufficient funds, remain stable. A deliberate new attempt after adding funds needs a new key. Shape/auth/CSRF failures before entering the protocol do not reserve a key. Transient/ambiguous failures do not create a durable rejected business outcome.

Canonical intent includes command/version, server-derived owner, canonical account/original IDs, currency and amount where supplied, date intent, and meaningful immutable reason. It excludes JSON property order, insignificant amount zeros, generated IDs, current balances/statuses, and recording time. A server-resolved "today" value is not recomputed into the fingerprint on retry the next day; the first successful execution stores the resolved date in its receipt.

Retain key/hash/outcome evidence for the journal's lifetime in the MVP. Expiring keys can permit duplicate posting from a late retry. Any later retention/erasure policy must explicitly change that guarantee. Enforce per-owner quotas/rate limits and bounded receipts rather than promising infinite free storage.

Future HTTP adapters should preserve the existing ProblemDetails convention: 400 for invalid input/precision/CSRF, 401 for missing authentication, 404 for absent/foreign resources, 409 for state/idempotency conflicts, and a retryable unavailable response for transient/unknown outcomes. Do not expose SQL text or claim an ambiguous command definitely failed.

Native lock details: [SQL Server sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15).

## 10. Concurrency and archive races

Use existing Accounts rows as shared serialization anchors, even though no balance is stored there.

For each participant, issue a separate owner-scoped keyed read with UPDLOCK, HOLDLOCK inside the posting transaction. Acquire keys in a single documented SQL uniqueidentifier-compatible order, sequentially, never using Task.WhenAll on a DbContext. An ORDER BY in a multi-row query alone is not a proof of lock acquisition order. Row-level granularity is not guaranteed; escalation can reduce throughput.

Complete acquisition first, then aggregate movements in a separate statement. Under READ_COMMITTED_SNAPSHOT, that later statement gets a fresh committed snapshot; combining a waiting lock acquisition and an earlier snapshot calculation can use stale data. Do not use a long transaction-level SNAPSHOT for the funds decision.

Example: balance 100; two expenses of 80 with different keys. First writer holds the account anchor, validates, and commits balance 20. The second acquires the anchor afterward, reads 20, and durably rejects. If the first rolls back, the second sees 100 and can post. A transaction without this common serialization point can let both validate against 100.

Transfer locks both accounts, checks source sufficiency and destination headroom, and inserts one operation. Reverse-direction transfers use the same lock order. Deadlocks can still arise from other resources/FKs/indexes; bounded whole-command retry is required.

The existing archive SQL update needs an incompatible write lock on the same Account row. If posting wins, archive waits and then commits after that operation. If archive wins, the later ordinary posting re-reads Archived and rejects. Exact reversals use the approved exception. The current status-conditioned rename guard does not by itself protect financial funds.

AsNoTracking is not a locking/isolation mechanism. A rowversion on Account also would not detect journal inserts unless every posting updated the shared token. A mutable balance guard with conditional SQL updates is a possible later optimization, but adds synchronized state and reconciliation obligations.

These arguments depend on SQL Server 2019 behavior and compliant writers; prove them with independent-connection tests. See [SQL Server locking/versioning](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver15).

## 11. Failures, retry, and uncertain commit

| Failure point | Required outcome |
| --- | --- |
| Before transaction/after failed authentication or CSRF | No financial write or reserved key |
| Account/key lock timeout | Roll back; retryable unavailable/in-progress result, no asserted business failure |
| After operation insert, before audit/outcome/commit | Roll back all rows; retry same key |
| Process crash before commit | SQL rollback releases locks; no confirmed operation |
| Commit succeeded but HTTP response lost | Same key returns original operation/receipt |
| Connection lost during commit | Outcome unknown; resolve owner/key using a fresh connection |
| Deadlock victim | Roll back and retry the complete command with the same key and fresh DbContext |
| Same key, different intent | Conflict; no new operation |
| New key, original already reversed | Business conflict; unique reversal index is final defense |
| SQL range/conversion/invariant bug | Roll back, fail safely, investigate; never round to success |

Do not map every unique-key error to a successful retry: distinguish the idempotency key, one-opening, one-reversal, and unexpected key conflicts after rollback.

If EF retry strategies are enabled later, replay the entire idempotent transaction delegate, including locks and audit, with a fresh context. Do not retry an isolated insert/SaveChanges while assuming the earlier commit failed. Avoid relying on savepoint recovery with MARS; verify its setting and rollback the whole unit on protocol failure.

References: [EF transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions) and [commit ambiguity/execution strategies](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency).

## 12. Balance consistency and reconciliation

For an initialized account, balance is SUM(signed movements) across all confirmed operations and compensations for that owner/account/currency. There is no mutable balance value to drift from the journal. A zero-initialized account has a recorded zero; an uninitialized one has no asserted opening balance.

Current balance includes all posted rows; as-of effective-date queries filter each operation and reversal by its own date. Do not remove an original globally merely because it now has a reversal.

Reconciliation must independently recompute effects from raw canonical rows and reversal references, rather than invoke the same view twice. Check:

- Required opening existence, uniqueness, and effective-date ordering.
- Same-owner/currency references and valid row shapes.
- Transfer and transfer-reversal net effect equal zero per owner/currency.
- One reversal, exact inverse effects, no reversal cycles.
- Posted outcomes and audit links for every operation, with receipts matching their referenced operation; rejection outcomes have no operation.
- Bounds/nonnegative running balances under the approved date policy.
- View/API results equal the independent calculation.
- Global per-currency balance change equals net opening contributions (including opening reversals) plus net income minus net expense; internal transfers cancel. Compare exact totals, not a potentially scale-reduced decimal residual.

Run related checks in one RCSI statement, or use an explicitly enabled SNAPSHOT transaction for multi-query consistency. Do not assume ALLOW_SNAPSHOT_ISOLATION is enabled because RCSI is enabled. No NOLOCK/READ UNCOMMITTED reconciliation.

Do not use a naive maximum identity watermark for incremental repair: an unrelated lower-sequence transaction may commit later. Full snapshot reconciliation is adequate initially. A later cache requires a consistent capture/catch-up protocol and rebuild tests.

On mismatch, report minimal identifiers/codes, block affected new posting if financial correctness is uncertain, and investigate. Do not auto-delete history, overwrite balances, or invent an adjustment to hide the discrepancy. This is internal simulation reconciliation, not reconciliation against a bank statement or external settlement system.

## 13. Worked simulated operations

All amounts below are DOP; A and B have the same owner and distinct identities.

| Step | Canonical operation | A balance | B balance | Reporting effect |
| --- | --- | ---: | ---: | --- |
| 1 | Opening A 1000; opening B 100 | 1000 | 100 | Opening principal only |
| 2 | Income 500 to A | 1500 | 100 | Income +500 |
| 3 | Expense 200 from A | 1300 | 100 | Expense +200 |
| 4 | Transfer 300 A to B | 1000 | 400 | No income/expense |
| 5 | Full reversal of step 4 | 1300 | 100 | No income/expense |
| 6 | Full reversal of step 3 | 1500 | 100 | Expense -200 |
| 7 | Full reversal of step 2 | 1000 | 100 | Income -500 |
| 8 | Full reversal of A's opening | 0 | 100 | Opening principal -1000 |

Original rows remain visible. The last step leaves A initialized at zero; it does not permit a second opening under this minimal proposal.

Other important outcomes:

- Transfer DOP A to a USD account: reject, with no partial effect.
- Income 100 followed by expense 80: reversing that income rejects at current balance 20.
- Replaying step 4's original key after steps 5-8 returns step 4's receipt; it does not repost.
- Reversing step 4 again with a different key: already-reversed conflict.
- Ordinary expense from Archived A: reject. An eligible exact reversal may proceed under Q4.
- Nonzero incoming movement exceeding Bmax: reject atomically, including a transfer destination.
- Reversal followed by replacement with a different amount uses a new operation/key and does not erase either original or reversal.

## 14. Test strategy

### Pure domain/application tests

Verify operation shapes, positive/zero rules, same-account transfer rejection, cross-currency errors, exact reversal references, identity/date immutability, original classification, no double compensation, numeric boundaries, and canonical fingerprints. Include alternative JSON order/currency case/trailing zeros, and exclusion of generated timestamps.

### Real SQL and HTTP integration tests

Verify actual mappings, composite FKs, CHECK null semantics, filtered uniqueness, runtime denial of financial UPDATE/DELETE, ownership/BOLA, CSRF, archived behavior, zero opening, all-or-nothing operation/outcome/audit writes, exact receipt replay, and ProblemDetails. Round-trip decimal values and SUM outputs through the exact EF/SqlClient versions; the BCL diagnostic is not a substitute.

### Independent-connection concurrency/failure tests

| Scenario | Asserted result |
| --- | --- |
| Two expenses 80 against 100 | Exactly one commits; other rejects; final balance 20 |
| Many identical same-key requests | One operation/outcome/audit; same receipt |
| Same key, differing intents | One winner; other conflict; no second effect |
| Opposite-direction transfers | Conserved totals, no partial effects, bounded deadlock recovery |
| Concurrent opening | Exactly one initialization, including zero |
| Concurrent full reversals with different keys | One inverse, one already-reversed outcome |
| Archive wins vs posting wins | Result follows actual account-lock serialization |
| Reversal vs spending | No negative result; one sees the other's committed effect |
| Fail after each write, before commit | No orphan operations/outcomes/audits |
| Commit then lost response | Durable receipt replay; no duplicate |
| Dropped connection during commit | Resolve ambiguity by owner/key, not guessed rollback |
| Reconciliation during writes | Consistent snapshot; no false half-transfer |

The current shared SqlConnection/transaction HTTP fixtures are useful for sequential isolation but cannot establish these concurrency guarantees. New tests need independent connections, real commit/rollback boundaries, barriers/timeouts, and final committed-state assertions. Do not wrap all concurrent workers in one shared ambient transaction.

Prefer an explicitly provisioned isolated SQL Server test database with run markers and least-privilege runtime/migration roles. Any future create/drop cleanup must validate the exact local server, test-only database name, and run ownership; never repurpose the current fixture to drop VaultFinanceDb or a system database. No Docker or EF InMemory is necessary. Test infrastructure changes need their own reviewed increment.

## 15. Small implementation increments

| Increment | Deliverable | Exit evidence |
| --- | --- | --- |
| 0 | Resolve Q1-Q8; reconcile future requirements and finalize ADR | Approved range, date, opening, archive, and failure semantics |
| 1 | Pure operation/reversal rules and canonical intent specification | Deterministic unit tests; no endpoints/database |
| 2 | Independent-connection SQL test environment; decimal round-trip spike | Exact boundary and isolation evidence; safe cleanup proof |
| 3 | Reviewed additive schema/view/constraints/roles migration | SQL constraint/view/permission tests; initial migration untouched |
| 4 | Atomic posting boundary, key/account locks, outcomes/audit | Concurrent zero-opening and fault-injection tests; no public financial route |
| 5 | Opening initialization and balance query | Explicit uninitialized/zero behavior and audited replay |
| 6 | Income and expense | Concurrent overspending/overflow tests; no negative balances |
| 7 | Same-owner same-currency transfer | Conservation, ownership, archive-race and rollback tests |
| 8 | Full reversals | Single compensation, funding checks and archived exception tests |
| 9 | Authenticated HTTP adapters and operational reconciliation | Real CSRF/BOLA/receipt tests, bounded errors and observability |

Each increment preserves domain/auth/account regressions, /health independence, and explicit migration review. No automatic startup migrations. A financial endpoint is not complete merely because a sequential happy-path test passes.

## 16. Risks and questions before programming

| ID | Decision required | Provisional recommendation |
| --- | --- | --- |
| Q1 | Must every currently valid Money value persist, or may financial commands have an explicit maximum? | Approve bounded decimal(28,2), or prove a full-range alternative before schema design. |
| Q2 | Are negative openings, overdrafts, liabilities, or credit accounts in scope? | Funded simulated accounts only; reject negative opening/result. This is a new policy, not an existing guarantee. |
| Q3 | Which accounting timezone and effective-date/backdating rules are required? | One configured calendar; UTC provisional; current-date postings, no backdating/scheduling. |
| Q4 | May accounts with nonzero balances be archived, and can they receive compensations? | Preserve current archive behavior; keep balances visible; permit exact full reversals only. |
| Q5 | How must an incorrect opening be replaced, beyond cancelling it to zero? | Decide explicitly; minimal schema supports one opening and its full cancellation, not arbitrary opening adjustment. |
| Q6 | Are full reversals enough, and must reverse-plus-replacement be one atomic action? | Full reversals only; separate commands initially, with no claim of bundled atomicity. |
| Q7 | What key namespace, idempotency/audit retention, and user-erasure policy are required? | Owner-wide financial keys; retain evidence with journal lifetime; no silent TTL that weakens replay guarantees. |
| Q8 | Is one trusted posting adapter sufficient, or must the database defend against other INSERT writers? | Trusted adapter plus append-only role initially; stored-procedure-only writes if the threat model requires stronger enforcement. |

Other risks: aggregate-query cost as history grows, SQL Express capacity, hot-account serialization, lock escalation/deadlocks, clock regressions, oversized numbers at browser boundaries, production key management, and future category/reporting semantics. Measure before introducing balance projections, distributed infrastructure, or a full ledger.

## 17. Validation of this design task

Repository code, docs, mappings, and relevant tests were inspected. Official SQL Server, EF, and .NET sources were consulted for numeric and concurrency behavior. A read-only local BCL numeric probe was run; no SQL connection or database mutation was needed.

This task creates only this document and ADR-002. It does not run restore/build/test or claim new passing financial tests, and it does not alter existing domain/migration/configuration files. Future guarantees remain design obligations until implemented and demonstrated with the tests above.
