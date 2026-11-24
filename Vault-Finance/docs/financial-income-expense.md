# Financial Income and Expense

Status: implemented Application cases and controlled SQL writes; no financial HTTP endpoints.

This increment extends [ADR-002](decisions/ADR-002-financial-operation-journal.md), the [engine design](financial-engine-design.md), [journal schema](financial-journal-persistence.md) and [opening protocol](financial-opening-writer.md). Vault Finance records simulated personal finance, not bank funds or payments.

## Contracts and invariants

IFinancialAccountService adds RecordIncomeAsync and RecordExpenseAsync. Both take a separate FinancialOwner, FinancialEntryRequest and idempotency key. The request contains AccountId, decimal Amount, Currency and optional Reason only. There is no client owner, kind, recording date, effective date, category, merchant or budget field.

The trusted caller must obtain FinancialOwner from authenticated server state. This context is not authentication by itself and must never be bound from request data. Foreign/missing account IDs both produce NotFound.

FinancialAccountService reuses the existing identifier/key validation and unchanged Money/FinancialAmount. Amount must be strictly positive, at most 99999999999999999999999999.99, with at most two effective decimal places. Currency is normalized DOP/USD. Optional reason is trimmed, whitespace-only becomes null, and its maximum remains 200 UTF-16 code units.

Every new entry requires an owned, Active, initialized account and matching currency:

- Income adds its positive magnitude, provided the final balance stays within the supported bound.
- Expense subtracts its positive magnitude, including exactly all available funds. A negative final balance is rejected.
- Current corrupt/incomplete/out-of-range history fails closed with JournalInconsistent.
- Income causing prospective overflow returns BalanceLimitExceeded; an excessive expense returns InsufficientFunds.
- Account without a financial root returns Uninitialized. Explicit zero opening is initialized and remains distinct from no opening.

FinancialEntryReceipt contains the original account/batch/operation IDs, kind, magnitude/currency and UTC/accounting metadata. It has no current Balance or BalanceAfter. A later balance is a separate owner-scoped read. Confirmed rows remain immutable.

```csharp
using VaultFinance.Application.FinancialAccounts;

var owner = new FinancialOwner(authenticatedUserId);
var income = await financialAccounts.RecordIncomeAsync(
    owner, new FinancialEntryRequest(accountId, 500m, "DOP", "Synthetic income"),
    incomeKey, cancellationToken);
var expense = await financialAccounts.RecordExpenseAsync(
    owner, new FinancialEntryRequest(accountId, 125m, "DOP"),
    expenseKey, cancellationToken);
var current = await financialAccounts.GetBalanceAsync(owner, accountId, cancellationToken);
```

There are still no Transfer, Reversal or opening-correction use cases or endpoints.

## Shared implementation rather than parallel writers

Application retains FinancialAccountService and the narrow IFinancialJournal port. SqlFinancialJournal adds the two entry methods and shares its existing connection/parameter/retry/result pipeline through a private ExecuteWriteAsync helper. Opening and balance interfaces remain compatible; no generic repository, mediator or new package is introduced.

The additive 20261124145702_FinancialIncomeExpenseWriter migration adds:

| SQL object/change | Responsibility |
| --- | --- |
| ExecuteFinancialWrite | Internal transaction owner for exactly InitializeOpening, Income and Expense |
| InitializeFinancialOpening, altered through the new migration | Compatibility wrapper selecting command 1 |
| RecordFinancialIncome | Public wrapper selecting command 3 |
| RecordFinancialExpense | Public wrapper selecting command 4 |
| Two EXECUTE grants | Extend the existing restricted financial runtime role |

Financial command scopes are not journal kinds: InitializeOpening=1 maps to Opening=1; Income=3 maps to Income=2; Expense=4 maps to Expense=3. The caller cannot supply a command kind to a public wrapper. The internal dispatcher is not granted to the runtime role and rejects unsupported command values even for an administrative caller.

AcquireFinancialAccountLocks, ReadLockedFinancialBalance, the effects view, table mappings, constraints, immutable-history triggers and direct-DML denials are reused unchanged. No tables, columns, indexes or mutable balances are added. Previously applied migration files are untouched.

The opening wrapper retains its parameter contract, scope, receipt shape, result codes and exact v1 canonical hash bytes. The complete existing opening tests run against the shared writer, including its native timeout retry, permission and rollback probes.

## Transaction, funds and exact arithmetic

The writer owns one READ COMMITTED SQL transaction with XACT_ABORT ON, native five-second lock waits and transaction-owned request/account locks. It rejects an ambient transaction. The sequence is:

1. Validate wide monetary/text parameters and trusted owner existence before reserving a key.
2. Acquire the exclusive owner/command/key application lock and resolve replay/conflict.
3. Acquire the existing owner-scoped Account PK U/HOLDLOCK anchor.
4. Re-read owner, Active state, currency, initialization and exact derived balance while holding that anchor.
5. Compute the prospective final balance and reject insufficient funds or receiving overflow.
6. Capture trusted UTC/accounting metadata and reject a successful candidate whose recording instant or accounting date regresses against that account's committed financial history.
7. Insert one batch, one operation, one immutable outcome and one audit. Entry commands add no opening revision.
8. Re-read/validate complete final history under the same account lock; commit only if its balance equals the expected exact result.
9. Return the stored receipt after commit.

The current validated balance and amount are decimal(28,2); their addition/subtraction produces decimal(29,2). That value is range-checked before narrowing. The implementation does not add decimal(38,2) aggregates, whose arithmetic could reduce scale. SUM remains handled by the existing guarded decimal(38,2) reader. Both Application and SQL reject meaningful excess monetary precision before numeric conversion could round it.

For initial balance 10000 and two expenses of 7000, the first successful writer commits 3000. The second acquires the account anchor afterward, reads 3000 and persists a rejection. Different idempotency keys do not bypass that anchor. With sufficient funds both serialize successfully.

Each positive entry has exactly one account effect: Income uses DestinationAccountId; Expense uses SourceAccountId. SQL's canonical operation, typed FKs and immutable triggers remain the source of truth. No operation/result/audit is updated after confirmation.

## Archive interaction and future transfers

Archive is currently an owner-scoped EF ExecuteUpdate of Accounts.Status, not a stored procedure. It obtains an exclusive write lock on the account row, which conflicts with the financial U-lock anchor. It does not need a new financial writer or an in-memory mutex.

Tests enforce both orders through real locks:

- Archive first: its EF transaction holds the status change while the financial request demonstrably waits. Once archive commits, the financial request re-reads Archived and rejects.
- Financial operation first: a test-owned audit gate holds the financial transaction after inserts while archive demonstrably waits. Releasing the gate lets the financial commit before archive; the final account is archived and retains the committed effect.

The existing helper orders one/two account IDs sequentially using native SQL uniqueidentifier order and remains suitable for a later transfer design. No transfer command is enabled by this increment. Lock escalation/deadlocks remain possible; ordering is not a claim of impossibility.

## Canonical idempotency and rejection policy

Scope stays (owner, server-selected command type, case-sensitive key). The same key can be used independently for Income, Expense and different owners. Different account IDs remain different payloads within one owner/type/key scope, not separate scopes.

SQL remains the fingerprint authority. New v1 headers are VF-INCOME and VF-EXPENSE. The remaining canonical UTF-16LE/no-BOM fields match opening: version, lowercase account Guid in D format, exact two-place amount, uppercase currency and a final length-prefixed trimmed reason. Null reason is encoded as -1:; an empty/whitespace reason normalizes to null before SQL. A non-null Unicode reason uses its UTF-16 code-unit length. Generated IDs, timestamps and later balances are excluded.

Matching retries return the original receipt or rejection before reevaluating mutable account status, balance or initialization. Changed valid payload conflicts without replacing the original. Invalid shape/amount/currency/key fails before key reservation.

Business rejections remain durable outcomes with audit and no financial batch/operation. An expense rejected for insufficient funds stays rejected with the same key after income arrives; a deliberate new attempt needs a new key. Likewise an overflow rejection remains stable after an expense creates headroom, and an Uninitialized rejection remains stable after opening.

Unexpected SQL/constraint/audit errors before commit roll back all inserts and do not consume the key. The original exception is not hidden. Existing bounded whole-protocol retries are shared: at most three attempts, new connections, 50/100ms backoff. Unknown commit responses are resolved using the same owner/type/key; exhausted ambiguous transport attempts return CommitOutcomeUnknown, never a claim of rollback. Cancellation and external delivery are not exactly-once guarantees.

## UTC, permissions and boundaries

SQL supplies UTC datetimeoffset(7), the approved America/Santo_Domingo accounting date using its verified Windows mapping, and a single timestamp for batch/outcome/audit. No input can backdate or schedule a movement. The new writer additionally rejects account-wide recording/effective-date regression; its tests simulate corrupt future history without changing any system clock.

VaultFinanceFinancialRuntime retains its direct SELECT/INSERT/UPDATE/DELETE denials for journal tables and view-read denial. It gains EXECUTE only on the two new public wrappers. A non-owner WITHOUT LOGIN probe proves both entry points work while direct DML and ExecuteFinancialWrite access are denied. Ownership chaining is static within dbo; the runtime receives no DDL privilege or free-form SQL path.

The production service must use an appropriately restricted principal. The current development administrator can bypass ordinary role restrictions and is outside the guarantee. Owner identity is authenticated server context, not database authentication of individual web users.

## SQL tests and isolation

Only the existing marked VaultFinanceJournalTests is used, with the existing SqlTestConnection/catalog/server/ownership guards. Creation is disabled. All test identities/accounts/history are synthetic and isolated by new Guid owners. Immutable committed history is retained; no reset, purge or deletion of financial records occurs.

Concurrency tests create independent actual connections, hold a separate native account lock and observe distinct blocked server session IDs before release. They cover:

- Identical Income and Expense retries with one effect.
- Same-key changed payload conflict.
- Two 7000 expenses against 10000: one approval, one rejection, final 3000.
- Two expenses with enough funds: both approvals.
- Concurrent Income/Expense with results matching one valid serial order.
- Both controlled archive/operation orders through the actual EF archive path.

Tests use DMVs and require VIEW SERVER STATE for that evidence; absence fails the concurrency test rather than being treated as success. Task.WhenAll or delays alone are not the proof.

Separate probes verify exact signed effects/current balances, unchanged opening revisions, current/cross-owner state rejection, full audit-failure rollback and same-key retry, payload fingerprints, stable replay after archive/funds/initialization changes, prospective overflow, permission denials and recording-clock regression. Fault/gate triggers and temporary principals are removed only after object ID/definition or SID ownership checks.

Physical network loss during commit, a forced native deadlock victim, production multi-node load, transfers, reversals and opening corrections remain untested/unimplemented scenarios. Native lock-timeout retry remains covered by the retained opening tests; the same adapter/pipeline now serves all three supported write types.

## Migration and commands

The [reviewed idempotent SQL](../backend/scripts/sql/FinancialIncomeExpenseWriter.sql) changes procedures/grants only. It is validated/applied only in VaultFinanceJournalTests. API startup does not migrate; VaultFinanceDb remains at InitialPersistence. The new migration is forward-only; no prior applied migration is edited.

From the Vault-Finance directory, after configuring the dedicated SqlTestConnection:

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    dotnet test VaultFinance.slnx --no-build --no-restore --logger "trx;LogFilePrefix=entries" --results-directory .local/test-results/income-expense
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build -- --environment Development
```

Final results: 175 unit and 278 integration cases passed (453 total; 0 failed; 0 skipped), preserving the previous 369 and adding 25 unit plus 59 real SQL cases. Restore/build completed with zero warnings/errors. EF reports no pending model changes. Read-only inspection confirms VaultFinanceDb still has InitialPersistence only and zero financial procedures; VaultFinanceJournalTests has all four migrations and seven financial procedures. No test-owned entry gate/fault triggers or temporary entry principals remain.

No new packages, HTTP endpoints, frontend, categories, merchants, budgets, Transfer/Reversal/correction execution, commits or pushes are part of this increment.

References: [SQL decimal arithmetic precision](https://learn.microsoft.com/en-us/sql/t-sql/data-types/precision-scale-and-length-transact-sql?view=sql-server-ver15), [transaction-owned application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15), [SQL lock behavior](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver15).
