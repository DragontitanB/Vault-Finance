# Internal Financial Transfers

Status: implemented and verified on SQL Server 2019 Express in the existing marked VaultFinanceJournalTests database. Updated: 2026-11-24.

This increment extends [ADR-002](decisions/ADR-002-financial-operation-journal.md), the [financial engine contract](financial-engine-design.md), [journal persistence](financial-journal-persistence.md), [opening](financial-opening-writer.md) and [Income/Expense](financial-income-expense.md). Amounts describe simulated personal finances, not bank funds. Domain, Accounts, Identity, journal tables, the effects view and previous migrations are unchanged. No financial HTTP route is introduced.

## Application contract

IFinancialAccountService.RecordTransferAsync takes a separate FinancialOwner, a FinancialTransferRequest, an idempotency key and optional cancellation. FinancialOwner must come from an authorized server context. The request contains SourceAccountId, DestinationAccountId, decimal Amount, Currency and optional Reason. It cannot supply owner, operation kind, timestamp or accounting date.

Application validates nonempty distinct account IDs, positive FinancialAmount limits, DOP/USD, at most two effective decimals, a trimmed optional reason of at most 200 UTF-16 code units, and the existing 1–128 ASCII letter/digit/hyphen/underscore key format. Whitespace reasons become null; currency is trimmed and uppercased. Invalid input never reserves a key. A ValidatedFinancialTransfer is a narrow Infrastructure port argument; SQL validates it again rather than trusting its name.

Success returns FinancialTransferReceipt v1: source/destination IDs, batch/operation IDs, Transfer kind, magnitude, currency, UTC timestamp and accounting date. It contains no later-changing balance. FinancialTransferOutcome uses the existing FinancialFailure codes. There is no new HTTP error mapping or endpoint.

Example inside a future authorized server use case:

```csharp
var owner = new FinancialOwner(authenticatedUserId); // Resolved by the server.
var intent = new FinancialTransferRequest(sourceAccountId, destinationAccountId,
    25.50m, "DOP", "Synthetic savings allocation");
var result = await financialAccountService.RecordTransferAsync(owner, intent,
    "transfer-demo-001", cancellationToken);

// Repeat the same intent/key to recover the original receipt, not to post again.
var replay = await financialAccountService.RecordTransferAsync(owner, intent,
    "transfer-demo-001", cancellationToken);
```

## Business rules and exact effects

- Both accounts must be owned by the trusted owner, Active, initialized and distinct, with the same DOP or USD currency matching the request.
- SQL resolves both owned participants before exposing state errors. Missing and foreign participants return the same NotFound failure in either position.
- Amount is 0.01 through 99999999999999999999999999.99 inclusive. No rounding, FX, commission, credit, cross-user transfer or overdraft is permitted.
- Source balance must be at least the amount; spending exactly all available funds is valid. Destination balance plus amount must not exceed the same per-account maximum.
- One posting batch contains exactly one canonical FinancialOperation, Kind=Transfer (4), with SourceAccountId and DestinationAccountId. CommandType=Transfer (5) is the distinct command/idempotency scope.
- The existing view derives exactly two effects: source −amount and destination +amount. No separately writable Income/Expense legs or mutable Balance column exist. Transfers do not become income or spending.
- All five history tables retain SQL append-only triggers and EF after-save guards. Confirmed transfers cannot be edited or deleted.

For A=100 and B=20 DOP, transferring 35.23 produces A=64.77 and B=55.23; total stays 120. Transferring A's remaining 64.77 leaves A=0 and B=120. An insufficient-funds or receiving-overflow rejection produces no batch, operation or effect.

Both validated balances and the magnitude use decimal(28,2). Prospective source/destination balances and the pair total use decimal(29,2); adding prospective values produces decimal(30,2) before comparison. Pair totals may exceed the per-account maximum, while each final account must remain bounded. SQL does not add decimal(38,2) aggregates to perform these comparisons, which could lose scale. The existing reader aggregates in decimal(38,2), range-checks and only then narrows. The tests include maximum amounts and a pair total greater than the single-account limit.

## Transaction and lock protocol

The public dbo.RecordFinancialTransfer wrapper selects fixed command type 5 and calls the existing private ExecuteFinancialWrite. Opening, Income and Expense continue using that same transaction owner and retry adapter. The procedure refuses ambient transactions, uses SET XACT_ABORT ON, READ COMMITTED and a 5000ms lock timeout.

1. Validate wide textual monetary/currency/key/reason inputs without SQL parameter narrowing; validate the trusted owner exists.
2. Begin one SQL transaction. Acquire an exclusive transaction-owned sp_getapplock scoped to owner/command/key.
3. Look up the immutable outcome. A matching fingerprint/version replays it before reevaluating mutable account state. Changed intent conflicts. Replay also requires its audit to exist.
4. Acquire both Accounts anchors with AcquireFinancialAccountLocks. It puts the distinct IDs in a private table and uses native SQL uniqueidentifier order. A cursor enumerates only that private ID table. Each iteration completes a separate owner-scoped singleton Accounts PK seek before fetching the next ID. UPDLOCK/HOLDLOCK/ROWLOCK plus parameterized FORCESEEK(PK_Accounts(Id)) prevents an Account scan from substituting a different acquisition order. This is not a single Accounts query whose ORDER BY is assumed to control physical locking.
5. Retain those locks through commit/rollback. Re-read both owned rows and validate state/currency. Read each complete initialized balance under its anchor. Validate source funds, destination headroom and conservation.
6. Capture one SQL UTC datetimeoffset(7); derive EffectiveDate through the verified SA Western Standard Time mapping of America/Santo_Domingo. Reject clock/accounting-date regression against relevant history on either account. No user-selected dates are accepted.
7. Append one batch, one canonical operation, the stored outcome/receipt and audit. A business rejection appends only its rejected outcome/audit. No opening revision is added.
8. Re-read both complete balances under the same anchors, require the exact expected balances and original pair total, then commit. Return the receipt only after commit. Unexpected pre-commit failures roll back all records, including key reservation.

The helper also serves single-account opening, entry and derived-balance procedures, so they use the same account anchors. Archive remains the real owner-scoped EF ExecuteUpdate path; its exclusive account lock conflicts with the financial update lock. Archive first leads to Archived rejection; transfer first posts before archive. Neither path relies on stale domain snapshots.

READ COMMITTED alone does not protect a derived aggregate. The retained account anchors provide serialization for compliant writers, including when database row-versioned reads are enabled. This guarantee requires every future financial writer to use the protocol and migration/runtime permissions to remain intact.

Ordering reduces account-lock cycles but does not eliminate all deadlocks. FK/index/history reads, missing-key ranges, lock escalation, administrative writes and future operations can acquire other resources. ROWLOCK is a request, not an absolute no-escalation guarantee. Tests also observed a second independent pair waiting on an uncommitted FinancialPostingBatches alternate-index key. Concurrent independent pairs complete with correct isolated effects; independent physical progress/throughput is not guaranteed. No NOLOCK, lowered integrity checks or new isolation mode was introduced to hide that contention.

## Idempotency, compatibility and recovery

The existing case-sensitive unique scope remains (UserId, CommandType, IdempotencyKey). A transfer key is independent of an Income/Expense/opening key or another owner's key. SQL's transaction-owned key lock protects lookup followed by insertion; this is not an unprotected check-then-insert.

Transfer fingerprint v1 hashes the following UTF-16LE bytes without a BOM using SHA-256, stored as uppercase hexadecimal:

```text
VF-TRANSFER\n1\n{lowercase source Guid D}\n{lowercase destination Guid D}\n{amount with exactly two decimal places}\n{uppercase currency}\n{reason suffix}
```

Here \n denotes LF, not the two literal characters. The reason suffix is -1: for null, otherwise {UTF-16 code-unit count}:{trimmed reason}; there is no extra delimiter after the suffix. Source/destination are retained in semantic order for hashing, independently of the sorted lock order. Inverting the direction changes intent. Owner/type/key scope, transient clock, generated IDs and current balances are not payload fields.

The old VF-OPEN, VF-INCOME and VF-EXPENSE canonical bytes, versions and receipt formats are unchanged. Their existing fingerprint/replay tests remain in the full suite. Matching transfer retries return the stored receipt or rejection without recalculating it; archive, subsequent income/expense or newly available headroom do not reinterpret an old request. Use a new key for a deliberate attempt after a business rejection. Outcomes and audit are retained for the MVP lifetime.

SqlFinancialJournal shares its existing maximum three whole-protocol attempts, fresh SQL connections, 50/100ms backoff, 15-second command timeout, lock/deadlock/unique-conflict classification and ambiguous-transport recovery. A deadlock victim or rolled-back attempt never retries just the remaining inserts. Retry the identical owner/type/key/payload after an unknown commit response. Exhausted unambiguous transient failures return TemporarilyUnavailable; exhausted ambiguous recovery returns CommitOutcomeUnknown. Do not create a new key to recover an unknown outcome. Cancellation and network response delivery are not exactly-once guarantees.

## Migration and permissions

20261124152847_FinancialTransferWriter changes two procedures and adds one public wrapper plus its EXECUTE grant. It changes no table, column, index, view, trigger or previous migration. The frozen resource and [reviewed idempotent migration script](../backend/scripts/sql/FinancialTransferWriter.sql) contain no DROP TABLE, DROP COLUMN, TRUNCATE or existing-table destructive operation. Down is intentionally unsupported; use a reviewed corrective migration.

VaultFinanceFinancialRuntime gains only EXECUTE on RecordFinancialTransfer. ExecuteFinancialWrite, AcquireFinancialAccountLocks and ReadLockedFinancialBalance remain private. Static dbo ownership chaining allows the public entry point to use those objects while direct history reads/inserts/updates/deletes stay denied. No new DDL rights or dynamic client SQL are granted. The non-owner WITHOUT LOGIN test principal verifies permitted transfer/balance calls and SQL 229 for direct DML/private procedure access, then is removed only after name/SID/catalog ownership checks.

The application must use a restricted deployment principal. A development sysadmin/dbo can bypass ordinary permissions and is outside this protection. The trusted backend supplies owner identity; database procedures do not independently authenticate web users. No connection strings, request payloads, tokens or credentials are logged.

Only the existing marked VaultFinanceJournalTests was migrated. VaultFinanceDb remains at InitialPersistence. API startup does not migrate. The fixture checks exact catalog, allowlisted local server, Windows authentication, no attached files/failover, project marker, known tables and migration history before applying pending migrations. Creation was disabled; no database or persisted financial history was cleared. Test-only triggers are scoped to a fresh synthetic owner and removed only after object ID/definition-marker checks.

## Reproduce validation

From Vault-Finance, securely configure ConnectionStrings:SqlTestConnection in API User Secrets or ConnectionStrings__SqlTestConnection for the dedicated database. Do not paste its value into files, logs or command arguments. Preserve the approved TLS settings and use the existing trusted certificates.

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore

# Review without applying to any database.
dotnet ef migrations script 20261124145702_FinancialIncomeExpenseWriter 20261124152847_FinancialTransferWriter --idempotent --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build --output .local/FinancialTransferWriter.review.sql

# The guarded fixture applies pending migrations only to the marked test database.
$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    dotnet test VaultFinance.slnx --no-build --no-restore --logger "trx;LogFilePrefix=transfers" --results-directory .local/test-results/transfers
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build -- --environment Development
```

Do not run an unguarded database update against DefaultConnection. The initial-only development script is not a procedure for applying journal migrations. Migration promotion to the development database needs its own reviewed target/safety process.

## Verified evidence and limitations

Final validation passes 196 unit and 332 integration cases: 528 total, zero failed and zero skipped. All 453 prior cases remain; this increment adds 21 Application unit cases and 54 SQL integration cases. Restore and build complete with zero warnings/errors. EF reports no pending model changes; all five migrations are applied only on the dedicated test target.

The SQL tests verify DOP/USD, exact full-source expenditure, minimum/maximum precision/range, receiving overflow, total conservation, one canonical operation/two independently queried effects, each invalid participant in either position, owner non-disclosure, stable replay/rejection, command/owner scope, full canonical fingerprint, changed source/destination/direction/amount/currency/reason conflicts, and clock regression on either account.

Independent SQL connections and controlled locks/gates test simultaneous identical retries, overspending transfers, opposite directions, transfer versus Expense/Income, archive on either account in both forced orders, and independent pairs. DMV evidence observes distinct blocked SQL sessions before gates are released; waits or Task.WhenAll alone are not accepted as proof. The opposite-direction test also checks that neither writer holds an Accounts U/X key while the globally first SQL Guid is blocked. Concurrency probes require VIEW SERVER STATE; lack of evidence fails the test. A failed initial test expectation revealed additional journal-index contention; the final test asserts concurrent sessions and valid effects without claiming both pairs reach commit preparation independently.

A test-owned AFTER INSERT audit fault occurs after the operation/outcome inserts. Independent reads verify rollback of both effects, batch, operation, outcome and audit; the same key then succeeds after removal of the fault. The existing SQL immutability, historical hash/receipt, health, domain, authentication and account tests all remain passing.

Read-only independent inspection before/after confirms identical development object metadata, row counts and migration history: InitialPersistence only, with no financial procedures. The test database has five migrations and eight financial procedures; temporary transfer triggers/principals are absent after cleanup. No secrets or financial rows are removed for cleanup.

Physical network loss at commit, a deliberately forced native deadlock victim, exhaustive lock-escalation/query-plan stress and production multi-node load were not fault-injected. Existing native lock-timeout/retry tests cover the shared adapter; that is not proof of those untested scenarios. Future Reversal, opening corrections, HTTP authorization/CSRF for financial endpoints and deployment of a real restricted service principal remain separate increments.

References: [SQL table hints and parameterized FORCESEEK](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver15), [decimal arithmetic precision](https://learn.microsoft.com/en-us/sql/t-sql/data-types/precision-scale-and-length-transact-sql?view=sql-server-ver15), [transaction-owned application locks](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15).
