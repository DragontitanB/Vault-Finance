# Financial Engine Design

Status: Accepted architecture; Domain primitives and journal persistence implemented. Updated: 2026-11-23.

Vault Finance records simulated personal-finance assertions, not bank funds, custody, settlement, or credit. [ADR-002](decisions/ADR-002-financial-operation-journal.md) records the accepted architecture. The [journal persistence increment](financial-journal-persistence.md) adds storage/schema verified in a dedicated SQL database. There is still no financial writer, balance calculator, correction coordinator, Application command or endpoint.

## 1. Review and resolved contradictions

Money, Account and FinancialOperation remain unchanged. Money accepts signed/zero decimal values, DOP/USD, two effective decimal places, and the CLR decimal range. Account has immutable identity/owner/currency and Active/Archived states, but no balance, opening information, reactivation, or creation timestamp. EF now also maps Infrastructure journal records, preserving the Domain API and stored historical dates.

| Earlier proposal | Accepted resolution |
| --- | --- |
| Zero monetary Opening | Monetary magnitudes are strictly positive. Explicit zero initialization/replacement uses an auditable opening-version record with no zero-valued operation. |
| One Opening operation forever | One initialization root with immutable revisions; controlled correction may create replacement Opening operations. |
| Separate reversal/replacement commits | Opening correction is one indivisible posting batch and one durable result/audit outcome. |
| Validate each correction component as a standalone debit | Validate the complete batch's final balance vector; its uncommitted component prefixes are not observable financial states. |
| Owner-wide idempotency | Owner, server-selected command type, and key. |
| UTC calendar or user-selected historical opening | UTC instants; America/Santo_Domingo accounting date; no user backdating/scheduling. |
| Undecided overdrafts | No credit/overdraft accounts, negative openings, or negative confirmed balances. |
| Edit/delete confirmed transactions | Immutable originals and compensating operations only. |

These resolutions close the architectural contradictions. Unimplemented SQL guarantees remain subsequent verification gates, not assumed capabilities.

## 2. Model choice

Use an immutable canonical operation journal with derived movements/balances.

A transfer has one magnitude and both participants in one operation. Its two effects cannot be independently posted. Income/expense have one actual account effect without artificial counterpart ledger accounts. A reversal references its original rather than copying editable amount/account fields.

Full double entry remains an alternative for liabilities, splits, or accounting statements, but requires counterpart semantics and cross-row balancing while still needing funds checks, authorization, locking, and idempotency. It is deferred. Independent transfer rows, mutable balances as sole truth, generic repositories, event sourcing, and distributed coordination are not selected.

A posting batch is the SQL confirmation boundary. Ordinary monetary commands contain one operation; opening correction can contain two operations plus a revision. Zero initialization contains a revision/result/audit but no monetary operation.

## 3. Approved numeric, currency and date contracts

Financial operation magnitudes satisfy 0 < amount <= 99999999999999999999999999.99, corresponding to decimal(28,2); the minimum meaningful magnitude is 0.01. Confirmed per-account balances must be within [0, Bmax]. Generic Money keeps its existing wider signed/zero contract.

DOP/USD remain independent. Account/amount currencies must match; both transfer accounts must have the same owner/currency and distinct IDs. No FX or mixed-currency total is inferred.

Recording instants are explicit UTC DateTimeOffset values with zero offset. America/Santo_Domingo determines EffectiveDate. The future writer captures one trusted SQL UTC instant after locks and shares it across the complete batch. No client-supplied recording/effective date, backdating, or scheduling is accepted, including opening initialization/correction.

Domain construction receives a deterministic instant rather than reading a wall clock. It cannot prove that the caller used the trusted current clock. The writer must also reject account-wide effective-date/clock regression against committed history.

Timezone data must resolve the IANA identifier; no silent UTC fallback. Future persistence stores the effective date/timezone and must preserve those historical facts on materialization, rather than reinterpret them after timezone-rule updates.

UTC is not a commit timestamp or a perfectly monotonic clock. A bigint batch identity can have gaps and is not a global commit watermark.

## 4. Pure domain model implemented now

Namespace: VaultFinance.Domain.FinancialOperations.

| Type | Responsibility |
| --- | --- |
| FinancialAmount | Immutable Money wrapper enforcing positive bounded magnitudes. Money validates decimal precision and currency. |
| FinancialPostingContext | Immutable non-empty batch ID and explicit UTC instant; derives the Santo Domingo effective date. |
| FinancialOperationKind | Opening, Income, Expense, Transfer, Reversal. |
| FinancialOperationStatus | Confirmed only; no mutable Draft/Pending/Reversed workflow. |
| FinancialOperation | Sealed immutable operation descriptor created through validated factories. |

The descriptor contains identity, owner, context, kind/status, currency, optional magnitude, participant IDs, optional original ID/kind, and bounded immutable reason. It retains IDs, not mutable Account references.

Factories use account snapshots for local owner/currency/Active validation. They do not establish that these facts remain true at commit. The future SQL writer must reload them under locks.

Confirmed denotes the immutable domain representation, not proof of database durability, available funds, initialization, or global reversal uniqueness. Only a successful later writer commit establishes a durable journal fact. There is no edit/delete/confirm-again transition API.

## 5. Operation shapes and local invariants

| Kind | Representation | Future effect |
| --- | --- | --- |
| Opening | Positive magnitude, destination only; required reason | Destination +amount |
| Income | Positive magnitude, destination only | Destination +amount; income increases |
| Expense | Positive magnitude, source only | Source -amount; expense increases |
| Transfer | Positive magnitude, distinct owned same-currency source/destination | Equal opposite effects |
| Reversal | Original ID/kind only; no copied magnitude/participants; required reason | Exact inverse of original |

All IDs are non-empty. Reversal has the same owner/currency as its original, a distinct operation and batch ID, and cannot precede the original recording instant. Reversal of a reversal is forbidden. The reason is trimmed, at most 200 UTF-16 code units, required for Opening/Reversal; optional whitespace notes become null.

Ordinary factories require Active accounts. Reversal does not require Active snapshots: its immutable original supplies the reference, while the controlled writer later checks ownership, funds, and eligibility. It never reactivates or changes Account.

Use existing DomainException for invalid business values and ArgumentNullException for missing required object dependencies, consistent with Money arithmetic. No framework-specific error types belong in Domain.

Two independent reversal descriptors can be locally valid for one original. They are not two allowed postings: the writer and a global SQL unique constraint must reject the second. No in-memory registry or mutable original flag claims that guarantee.

## 6. Derived balance contract

For an initialized owner/account/currency, balance at a cutoff is the sum of signed effects from complete committed posting batches through that cutoff.

No opening revision means Uninitialized, not silently zero. An explicit zero initialization is Initialized at zero but has no zero monetary operation. There is no mutable Account.Balance, balance cache, pending settlement, or bank-available balance.

Every committed batch leaves each participant within [0, Bmax]; validate receiving headroom as well as outgoing sufficiency. Transfers and their reversals cancel per owner/currency. Opening principal/corrections are excluded from income/expense; income reversals reduce income and expense reversals reduce expense.

The batch is simultaneous for balance/reporting. Do not expose a cutoff or BalanceAfter between its components. Historical balances order complete batches, not arbitrary component rows.

Example: opening 100, expense 80, current balance 20. Correction to opening 80 creates compensation -100 and replacement +80 atomically. Final balance 0 is valid; the algebraic subtotal -80 is not a separately committed state. A replacement of 70 would leave -10 and must reject the entire batch without consuming the original's reversal.

Reversals take the current effective date; do not hide originals globally or rewrite their original reporting period.

## 7. Opening initialization and corrections

Do not implement the old unique index that permits only one monetary Opening forever.

Future FinancialOpeningVersions provides one immutable revision chain per owner/account/currency:

- Revision 1 initializes exactly once.
- Later revisions reference the immediately preceding revision.
- A positive revision references its new Opening operation.
- A zero revision explicitly has no Opening operation.
- Old versions and operations never change.

The future CorrectOpening command carries the expected current revision, desired nonnegative bounded opening amount, reason, and idempotency key. Under the account lock it validates that revision and constructs:

| Previous | Target | Atomic contents |
| --- | --- | --- |
| Positive | Positive, different | Full reversal + new Opening + next revision |
| Positive | Zero | Full reversal + next zero revision |
| Zero | Positive | New Opening + next revision; no fake zero reversal |
| Uninitialized | Zero or positive | InitializeOpening: first revision and a positive operation only when needed |
| Unchanged value | Same | Stable no-change conflict; no redundant financial rows |

A stale expected revision conflicts. The writer validates the final balance vector and commits all components/revision/result/audit together.

A standalone reversal of the current opening must append the next zero revision in the same batch. It cannot silently leave the current opening revision pointing at a cancelled operation. An obsolete opening is not an alternative correction target.

Archived accounts allow qualifying exact cancellation/reversal if the final balance stays nonnegative. A positive opening replacement requires Active status. No account reactivation is introduced.

Domain contains positive Opening/Reversal descriptors. Infrastructure now stores opening-version metadata, including explicit zero revisions. No zero-initialization command, correction service or batch coordinator is implemented.

## 8. Precision and mapping obligations

decimal(28,2) has 26 integer digits and a coefficient that fits CLR decimal across the column range. This is an approved financial business bound, not a change to Money. Full-range generic Money must not silently be stored in a narrower column.

Provider round trips for the maximum, trailing zeros and UTC/date facts are now proved against the dedicated SQL Server test database. SQL parameter conversion can round: reject 1.001 before binding, preserve insignificant zeros such as 1.2300, and never route values through float/double. The real SQL test explicitly demonstrates the raw-conversion limit; the future writer must retain early validation.

SUM(decimal) returns decimal(38, scale). Range-check the aggregate before conversion. Do not add unchecked decimal(38,2) aggregates: the result may need scale reduction. Cast validated bounded balances to decimal(28,2) before arithmetic; two operands produce decimal(29,2). Correction intermediates may be signed/wider but must retain scale 2 and satisfy the final bound.

Compare exact reconciliation totals, not a potentially rounded residual. Future HTTP monetary strings must use bounded invariant parsing with no exponent/group coercion or loss of meaningful excess digits.

Future currencies requiring other scales need an explicit Money/currency/schema/contract revision. A wider SQL scale cannot extend current Money by itself.

References: [SQL precision/scale](https://learn.microsoft.com/en-us/sql/t-sql/data-types/precision-scale-and-length-transact-sql?view=sql-server-ver15), [SUM types](https://learn.microsoft.com/en-us/sql/t-sql/functions/sum-transact-sql?view=sql-server-ver15), [IANA zones in .NET](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.findsystemtimezonebyid?view=net-10.0).

## 9. Relational schema and writer obligations

InitialPersistence and Domain remain untouched. Accounts gains only an owner/currency alternate key supporting financial FKs. Financial FKs use NO ACTION and the five journal tables have append-only EF guards and SQL triggers. The new migration is validated in VaultFinanceJournalTests only, not applied to VaultFinanceDb. See financial-journal-persistence.md for exact mappings, indexes, review and commands.

| Implemented relation | Purpose |
| --- | --- |
| FinancialPostingBatches | Owner, command type, UTC/effective date/timezone, batch ID and sequence; indivisible confirmation boundary |
| FinancialOperations | Owner/currency/batch, kind/reason, positive ordinary magnitude/participants or original reversal reference |
| FinancialOpeningVersions | Account/revision chain, predecessor, batch and optional positive Opening reference; null reference explicitly means zero |
| FinancialRequestOutcomes | Owner/type/key, fingerprint/version, immutable posted/rejected receipt and optional posted batch link |
| FinancialAuditEntries | One immutable event for each newly finalized outcome, linking to the complete batch through that outcome |
| AccountMovements projection | Read-only signed effects of ordinary rows and original-reference inverses |

Required constraints/indexes, implemented with the cross-row limits below:

- Account candidate key (UserId, Currency, Id), preserving current types/collation.
- Same-owner/currency/batch composite FKs and immutable original references.
- Explicit null/shape checks: ordinary positive magnitudes, distinct transfer participants, no copied reversal amount/accounts, valid kind/UTC/range/size limits.
- Non-reversal self-FK discriminator and filtered unique (UserId, OriginalOperationId), across all command types.
- Unique opening (UserId, Currency, AccountId, Revision); one root at revision 1; predecessor FK/check for revision minus one.
- Positive version references tied by FK to Opening kind and the same owner/currency/batch. The writer must additionally check that its destination equals the revision's account. No zero monetary row.
- Unique (UserId, CommandType, IdempotencyKey); same-owner receipt/batch/audit references.
- Owner/account/effective-date/batch access indexes and original-reference lookup, without indexing large receipts/notes indiscriminately.

CHECK accepts UNKNOWN; nullable branches need explicit IS NULL/IS NOT NULL. Constraints alone cannot require complete cross-row batch/audit membership, current opening selection, status or funds sufficiency. The controlled writer validates those facts before commit.

Storage-record materialization preserves persisted historical dates without calling the Domain context constructor. No Domain rehydration bypass or current-account-based reverse mapper is added.

## 10. Controlled writer and idempotency

Use one Infrastructure financial command boundary backed by a permission-hardened SQL posting procedure. Runtime writes execute it instead of unrestricted direct financial INSERT/UPDATE/DELETE. Migration/admin permissions are separate; this is not a claim about today's Windows development login.

The procedure owns one SQL transaction, fixed supported command types and parameterized SQL, with no external calls while locked. Domain/Application validate early; SQL rechecks critical persisted facts. End-user identity is trusted authenticated server input; the procedure does not independently authenticate an HTTP user or protect against a sysadmin.

Command types are InitializeOpening, CorrectOpening, Income, Expense, Transfer, and Reverse. They are use-case/idempotency scopes, distinct from the five journal kinds. CorrectOpening can emit several kinds.

Scope is exactly (owner, server-selected command type, key). The same key in another type is a different scope. Changed intent within one scope conflicts. Fingerprints include normalized payload/version and expected opening revision, excluding transient recording time, generated IDs, current balances/status and the newly resolved current date.

Future keys are case-sensitive opaque ASCII values, 1-128 characters from letters/digits/hyphen/underscore, never trimmed or truncated. Use a binary SQL collation and a versioned canonical payload encoding with SHA-256 fingerprints. Treat fingerprints as intent identifiers, not authentication; do not store secrets or log raw request bodies. Authorization always precedes receipt lookup/replay, and a receipt must not disclose another owner's operation.

Protocol:

1. Authenticate, validate CSRF/DTO/key/precision before SQL.
2. Begin READ COMMITTED transaction; acquire the owner/type/key transaction-owned lock and inspect any final outcome.
3. Matching hash/version replays the original receipt; mismatch conflicts with no new batch.
4. Resolve owned participants/original and lock account anchors sequentially in deterministic SQL-Guid-compatible order.
5. Re-read status/currency/initialization (all ordinary participants must already be initialized), capture one trusted UTC instant and read fresh balances in a later statement.
6. Validate revision, reversal eligibility, complete effects and final [0, Bmax] balances.
7. Append batch/components/revision/result/audit and commit together. Rejections append only a final rejected result and audit.
8. Return success after confirmed commit; resolve ambiguous commit with a fresh connection and the same owner/type/key.

Keep key/fingerprint/results and audit for the MVP lifetime. No silent TTL. Business rejections remain stable; intentional new attempts use new keys. Pre-protocol shape/auth/CSRF failures do not reserve keys.

Receipts describe the original batch/operation IDs/revision, not a later-changing balance or authentication token. Replay after archive/reversal is valid and does not re-execute.

## 11. Concurrency and archived accounts

Account rows are common anchors. Use owner-scoped keyed UPDLOCK/HOLDLOCK reads inside the transaction, consistently ordered and sequential, before the separate fresh aggregate. AsNoTracking, Account rowversion and a transaction alone do not prevent journal-insert races.

Two expenses 80 against 100: first commits 20; second acquires the anchor afterward and rejects. Transfer locks both accounts and checks source funds/destination headroom. Opening corrections use the same anchor plus expected revision.

Archive's existing UPDATE conflicts with the anchor. If archive wins, ordinary posting/positive replacement rejects; if posting wins, archive follows. Exact reversals use the controlled exception. History/balance remain visible and no Account property is reactivated.

An original remains Confirmed after compensation. SQL uniqueness must prevent two full reversals even across different command types/keys and opening corrections. No process-local state can prove this.

Deadlocks/escalation remain possible. Bound waits and retry the whole idempotent unit with a fresh context/connection, never an isolated insert while guessing a previous commit failed.

## 12. Failures, reads and reconciliation

| Failure | Required behavior |
| --- | --- |
| Input/auth/CSRF rejection | No SQL effect/key reservation |
| Key/account timeout | Roll back; retryable unavailable result |
| Failure after a component/revision/audit write | Roll back the complete unit |
| Crash before commit | SQL rolls back and releases transaction locks |
| Lost response after commit | Replay the same durable receipt |
| Connection loss during commit | Resolve uncertainty by owner/type/key; do not assert rollback |
| Concurrent opening correction | One current revision wins; stale expectation conflicts |
| Second full reversal | Conflict; no second inverse |
| Range or invariant error | Roll back and investigate; never round into success |

Use complete committed batches for reads. RCSI gives statement consistency; consistent multi-query reconciliation needs explicitly enabled snapshot isolation or another reviewed protocol. No NOLOCK. A batch identity high-water mark is not a global commit watermark.

Independently recalculate canonical effects/reversal references/revision chains and compare with the read projection: ownership/currency, exact inverse/uniqueness, complete result/audit membership, final batch bounds, transfer conservation and net openings + net income - net expense. Include original/reversal dates individually; do not erase original history or auto-invent adjustments to hide mismatches.

Known limits remain no liabilities, FX, partial reversals, user backdating, general ledger, balance cache or MVP history-erasure workflow. Capacity, hot-account serialization, clock regressions, roles and commit ambiguity need later evidence.

## 13. Increment boundaries and validation

Current domain tests cover bounded amounts, each kind, identifiers, ownership/currency/Active snapshots, immutable metadata, UTC/Santo Domingo boundaries, causal reversal references and forbidden mutable transitions. Prior tests are retained.

Proved storage guarantees now include exact provider round trips, typed/scoped references, original-reversal uniqueness, owner/type/key uniqueness, append-only SQL behavior and migration/model alignment. Not yet proved: sufficient funds, initialization/current revision, complete batches, atomic opening replacement/transfer, operational idempotency, runtime SQL permissions or locking. Later writer tests require independent connections, actual commit boundaries and fault injection; sequential shared-transaction HTTP fixtures are insufficient.

| Increment | Exit evidence |
| --- | --- |
| 1: pure Domain, implemented | Local factories/value objects and immutability; no balance/correction service |
| 2: isolated SQL feasibility, storage verified | Exact decimal/date materialization and independent connections; runtime least-privilege writer roles still deferred |
| 3: additive schema/projection, implemented | Ownership keys, revision chains, positive amounts, one reversal and batch metadata; dedicated test database only |
| 4: controlled writer | Atomic outcomes/audit, scoped replay, locks and no partial batches |
| 5: openings/corrections | Zero metadata, expected revisions, replacement/net-balance rollback |
| 6: income/expense/transfer/reversal | Overspending races, conservation and archived exception |
| 7: HTTP/reconciliation | Cookie/CSRF/BOLA, consistent reads, diagnosis and fault injection |

ADR acceptance closes architecture decisions, not unimplemented writer guarantees. This persistence increment authorizes additive schema/mapping and dedicated SQL validation, but no development database application or financial command/endpoint. /health remains independent of SQL.

Current validation runs restore/build and the complete SQL-enabled suite against the separate marked database. No tests use the development database for destructive operations or cleanup.

Historical increment-1 validation: 126 unit and 29 ordinary integration tests passed; 32 opt-in SQL methods were skipped. Current persistence validation: restore/build succeeded with zero warnings/errors; 126 unit and 176 integration cases passed, with zero skips. The dedicated migration is applied; the development migration is not. See the persistence document for count expansion, initial failures and exact limits.

References: [SQL locks](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver15), [application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15), [EF transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions), [commit ambiguity](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency).
