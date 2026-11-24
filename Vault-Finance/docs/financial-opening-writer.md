# Initial Financial Opening Writer

Status: opening initialization and current derived-balance reads implemented; no financial HTTP endpoints.

This extends [ADR-002](decisions/ADR-002-financial-operation-journal.md), the [engine design](financial-engine-design.md) and the [journal schema](financial-journal-persistence.md). Vault Finance remains a simulated personal-finance journal.

## Scope and resolved boundaries

This document records the original opening increment. The subsequent [Income/Expense increment](financial-income-expense.md) extends the same protocol. Transfer, Reversal and opening correction still have no executable Application/SQL entry points. Money, Account and FinancialOperation are unchanged. No Balance column, pending operation, mutable original or generic repository is introduced.

Business rejection is a final result, not a partially successful financial operation: it persists only a rejected outcome and its audit. Technical failure before commit rolls back the complete transaction. A response lost after commit cannot undo confirmed history; resolve it through the same idempotency scope, never by guessing rollback or changing the key.

The effects view previously exposed structural rows inserted by administrators/schema probes. Published balance reads now fail closed on incomplete batch membership, wrong opening-account references, movements without initialization, negative totals and totals above the financial bound. They do not silently discard malformed history.

## Application contracts

FinancialAccountService implements IFinancialAccountService. Its narrow IFinancialJournal port is implemented by SqlFinancialJournal; neither Application nor Domain references SQL/EF/ASP.NET.

FinancialOwner is a separate, non-empty trusted server identity context. It is not a field of InitializeOpeningRequest. The caller must obtain it from authenticated server state; constructing a Guid context is not itself authentication. No HTTP route or client binding is added here.

The request contains only AccountId, decimal Amount, Currency and Reason; the idempotency key is separate metadata. Application normalizes currency through unchanged Money, trims the reason, rejects meaningful excess precision, negative/excessive amounts, empty IDs and malformed keys. SQL independently validates the canonical wide-string inputs before numeric/varchar narrowing.

Opening amounts range from zero through 99999999999999999999999999.99. Positive amounts create one Opening operation; zero creates no monetary operation. Both create one batch, revision 1, posted outcome and audit.

OpeningReceipt contains a version, account/batch/optional operation IDs, revision, opening amount/currency, UTC recording instant and accounting date. Replays return the original immutable receipt, not a later balance. DerivedAccountBalance distinguishes IsInitialized=false/Amount=null from explicit zero initialization. Owned archived accounts remain readable.

Example inside an authenticated server use case, with no HTTP endpoint implied:

```csharp
using VaultFinance.Application.FinancialAccounts;

var owner = new FinancialOwner(authenticatedUserId);
var opening = await financialAccounts.InitializeOpeningAsync(
    owner, new InitializeOpeningRequest(accountId, 0m, "DOP", "Initial simulated position"),
    idempotencyKey, cancellationToken);
var balance = await financialAccounts.GetBalanceAsync(owner, accountId, cancellationToken);
```

Foreign and missing accounts both return NotFound. Archived, currency mismatch, already initialized and changed-key-payload conflicts have explicit outcomes. Invalid pre-protocol input reserves no key. Valid business rejections replay even if account state later changes.

## SQL transaction and locks

The additive 20261123224234_InitialFinancialWriter migration creates four procedures and one role; it changes no tables, columns or existing applied migrations:

| Procedure | Responsibility |
| --- | --- |
| AcquireFinancialAccountLocks | Internal transaction-only helper; sequential PK UPDLOCK/HOLDLOCK/ROWLOCK reads in SQL uniqueidentifier order |
| ReadLockedFinancialBalance | Internal helper requiring the caller's account lock/transaction; validates relevant history and exact aggregate |
| InitializeFinancialOpening | Public runtime entry point for one complete initial-opening command |
| GetDerivedFinancialBalance | Public runtime entry point for an owner-scoped consistent current read |

The initial migration grants the last two to the runtime role; the subsequent Income/Expense migration grants its two public entry points and routes writes through the shared internal ExecuteFinancialWrite. Helpers are reused through same-owner static SQL ownership chains, not exposed as arbitrary application lock APIs.

InitializeFinancialOpening now delegates to the shared ExecuteFinancialWrite through a fixed opening command, preserving the following READ COMMITTED/XACT_ABORT transaction protocol and its original v1 payload/receipt contract. It:

1. Validates wide inputs and trusted owner existence before reserving a key.
2. Takes a transaction-owned exclusive sp_getapplock for owner/InitializeOpening/key.
3. Reads the immutable stored result under that protected scope; replays matching fingerprint/version or returns conflict.
4. Locks the owned Account PK before status/currency/initialization/history validation.
5. Captures trusted SYSUTCDATETIME with zero offset after the account lock.
6. Appends batch, optional positive operation, revision, outcome and audit together; rejects duplicate roots.
7. Revalidates complete final history and exact balance before COMMIT.
8. Returns the result only after commit.

Unique outcome scope and opening revision keys complement the lock protocol. No unprotected check-then-insert is used. No external calls occur under SQL locks.

Account locks persist until commit/rollback. The helper sorts two IDs in native SQL order, which differs from CLR Guid.CompareTo, and acquires them sequentially on PK_Accounts. Its two-account probe tests reversed input order; this does not implement a transfer writer. ROWLOCK is a hint, not a promise against lock escalation/deadlocks.

Balance reads use the same U-lock anchor inside a short transaction. A shared lock alone would be compatible with a writer's U lock and would not serialize initialization and aggregate statements. This protocol remains consistent under RCSI without enabling/changing database isolation settings.

Native waits are bounded to five seconds; commands allow fifteen seconds. The adapter makes at most three complete attempts, with a new SqlConnection and 50/100ms backoff. Deadlock victim 1205, lock timeout 1222 and application-lock failures are retryable. A timeout/transport loss may have followed commit: retries use exactly the same payload/key, and exhausted ambiguous attempts return CommitOutcomeUnknown. Unambiguous exhausted lock failures return TemporarilyUnavailable. Cancellation is honored; a caller cancelling around commit must later resolve the same key.

Never retry only an INSERT, change keys after an unknown commit, swallow unexpected SQL failures, or claim a network error proves rollback.

## Canonical fingerprint and replay

Scope is exactly (UserId, server-selected CommandType=InitializeOpening/1, case-sensitive key). Keys are 1-128 ASCII letters/digits/hyphen/underscore. SQL checks Unicode code points before conversion; trailing spaces, Unicode lookalikes and null characters cannot collapse into another key.

SQL is the fingerprint authority. No independently mutable client fingerprint is accepted. Version 1 hashes UTF-16LE bytes without BOM of:

```text
VF-OPEN\n
1\n
{lowercase Guid in D format}\n
{canonical decimal with exactly two fractional digits}\n
{uppercase DOP or USD}\n
{reason UTF-16 length}:{trimmed reason}
```

The displayed \n values denote one LF character each, not a literal backslash. The final reason is length-prefixed and preserves its internal Unicode/whitespace/case. No Unicode NFC conversion is inferred. SHA2_256 is stored as 64 uppercase hexadecimal characters, FingerprintVersion=1.

Generated IDs, wall-clock/accounting dates, mutable account state and current balance are excluded. Decimal insignificant zeros and currency outer whitespace/case normalize identically. Malformed requests fail before lookup; changed valid intent in an existing scope conflicts without replacing the original outcome/audit. Each newly finalized result has one audit; replay/conflict does not add another financial event.

## Decimal and time guarantees

The adapter preserves all meaningful decimal digits in a wide string even when Application validation is bypassed. SQL requires an exact canonical round trip before decimal(28,2) use, so 1.001 cannot round into a valid opening.

Balance SUM remains decimal(38,2) until range validation against [0, Bmax]. Only then is it narrowed to decimal(28,2) and read as CLR decimal. Negative, financial-range and CLR-range corruption probes fail with JournalInconsistent; they do not publish a rounded/overflowed balance. SQL aggregate overflow 8115 is also treated as inconsistency.

The writer stores UTC datetimeoffset(7), the resulting date and the IANA zone name. SQL Server's SA Western Standard Time maps to America/Santo_Domingo; tests verify the .NET mapping and both sides of the 04:00 UTC accounting midnight. Inputs cannot supply recording/effective dates. Existing stored dates are not recalculated on materialization.

For a first opening there is no earlier initialized financial history; movements without a root are rejected. Future commands must implement the accepted cross-operation clock-regression checks rather than assume this initialization proof covers them.

## Permissions and threat model

The initial role grants opening/read; the subsequent migration adds Income/Expense execution only and explicit DENY for direct SELECT/INSERT/UPDATE/DELETE on the five financial tables, plus direct SELECT on the effects view. Static same-dbo ownership chains allow the procedures' necessary access. There is no dynamic runtime DML, generic command dispatcher, runtime schema grant or membership assignment to the development Windows identity.

A test-only WITHOUT LOGIN principal proves direct history access and helper execution are denied while opening/balance procedures succeed. It is removed only after verifying its generated name/SID; fault triggers are removed only after object ID/definition ownership checks. No financial records are deleted.

A deployment must use a non-owner, non-sysadmin service principal with these grants and no schema/permission-changing privileges. The current development/migration administrator can bypass role restrictions or disable triggers and remains outside the runtime guarantee. The supplied owner is authenticated server context, not database row-level human authentication; a compromised backend principal can misuse that trust.

## Tests, isolation and limits

All SQL tests use the existing marked VaultFinanceJournalTests through SqlTestConnection, with the established server/catalog/ownership checks. Creation is disabled for this increment. No migration is applied to VaultFinanceDb, and API startup still never migrates.

New opening tests commit synthetic users/accounts before opening calls; every adapter attempt uses its own connection. Concurrency tests hold a separate account lock and observe two different blocked SQL session IDs through DMVs before release. They cover identical replay, conflicting payload and different keys for one account; they require VIEW SERVER STATE for that proof and fail rather than silently treating sequential execution as concurrency.

A real five-second lock timeout exercises whole-unit retry. A test-owned AFTER INSERT audit trigger raises a controlled exception after other inserts: all five financial record groups roll back, the key is not consumed and a later retry succeeds once.

Administrative integrity probes deliberately bypass the runtime role only in the isolated database. They seed incomplete/wrong-reference, negative and excessive histories, including 800 maximum magnitudes exceeding CLR decimal. These are corruption probes, not implemented Income/Expense commands. All committed synthetic financial history remains retained; no reset/purge is performed.

Physical network loss during commit, a forced native deadlock victim, production multi-node load and future Transfer/Reversal writers are not fault-injection tested here. Income/Expense now have their own funds/archiving tests; see the subsequent increment. The adapter defines bounded recovery/unknown-result behavior; the current tests do not certify those future scenarios or exactly-once external delivery.

## Commands and artifacts

Configure SqlTestConnection as documented in financial-journal-persistence.md. Run from Vault-Finance:

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    dotnet test VaultFinance.slnx --no-build --no-restore --logger "trx;LogFilePrefix=opening" --results-directory .local/test-results/opening
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build -- --environment Development
```

The fixture applies only reviewed migrations to the marked dedicated database. The [idempotent writer SQL](../backend/scripts/sql/InitialFinancialWriter.sql) contains procedure/role/grant DDL and migration history only; existing schema/history is retained. The migration embeds frozen SQL and has no automatic downgrade. Future production/development application is a separate reviewed deployment; it was not performed here.

Results are recorded in README after the complete suite. The first cached NuGet audit attempt emitted NU1900; a forced restore without the stale HTTP cache outside the restricted environment completed successfully without disabling audit or changing packages.

Historical opening validation: restore succeeded; build had zero warnings/errors; 150 unit and 219 integration cases passed (369 total, 0 failed, 0 skipped). The 302 previous cases are retained. EF reports no pending model changes, and the generated SQL artifact matches the frozen migration. Read-only inspection confirms VaultFinanceDb still has InitialPersistence only and zero writer procedures; the dedicated database has all three migrations and four procedures. Test-owned fault triggers and temporary database principals were removed successfully.

For a later, separately reviewed development deployment, the explicit target command would be:

```powershell
dotnet ef database update 20261123224234_InitialFinancialWriter --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --context VaultFinanceDbContext -- --environment Development
```

That command was not executed against development. Review the secure target, both additive SQL scripts and the intended non-owner runtime principal before deployment; creating the role does not assign the privileged development Windows user to a safe production identity.

References: [sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15), [SQL permissions/ownership chains](https://learn.microsoft.com/en-us/sql/relational-databases/security/permissions-database-engine?view=sql-server-ver15), [SQL locking](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver15).
