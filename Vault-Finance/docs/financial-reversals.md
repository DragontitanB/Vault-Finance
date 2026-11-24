# Complete Financial Reversals

Status: implemented and verified in the existing marked VaultFinanceJournalTests database on SQL Server 2019 Express. Updated: 2026-11-24.

This increment implements the existing policy in [ADR-002](decisions/ADR-002-financial-operation-journal.md) and the [engine design](financial-engine-design.md), extending the shared [opening](financial-opening-writer.md), [Income/Expense](financial-income-expense.md) and [Transfer](financial-transfers.md) protocol. It records simulated personal finances, not bank funds. No new architecture decision is required; ADR-002 remains Accepted and unchanged. Opening correction and financial HTTP endpoints remain unimplemented.

## Review and scope

The current schema already supports exact reversals: a Kind=Reversal row references the immutable original ID/kind with the same owner/currency, has null magnitude/participants, and derives inverse effects through FinancialAccountMovements. Its existing filtered unique (UserId, OriginalOperationId) index limits each original to one reversal across keys and commands. No table, index, constraint, view or trigger needs changing.

FinancialOperation.CreateReversal permits an Opening descriptor because the accepted future opening-correction batch can require it. That Domain capability is not permission to execute a standalone opening reversal. This ordinary command explicitly rejects Opening and Reversal originals. It creates no opening revision and cannot perform a correction. Money, Account and FinancialOperation remain unchanged.

## Contracts and errors

IFinancialAccountService.RecordReversalAsync accepts a separate trusted FinancialOwner, FinancialReversalRequest, idempotency key and cancellation token. The request contains only OriginalOperationId and Reason. ValidatedFinancialReversal adds only the key. Neither input can supply amount, currency, account IDs, original kind, owner or recording/effective dates.

Application requires a nonempty original Guid, a trimmed reason of 1-200 UTF-16 code units, and the existing case-sensitive 1-128 ASCII letter/digit/hyphen/underscore key format. Invalid input reserves no key. The caller must obtain FinancialOwner from authorized server state; constructing that value is not authentication. No HTTP transport or client binding is introduced.

The SQL writer independently validates wide inputs. It retrieves all original facts from the owned persisted operation, and requires its batch to have a posted outcome and audit with compatible command type. Missing and foreign originals both return NotFound. Incomplete/incompatible history fails closed with JournalInconsistent and no new outcome.

FinancialReversalReceipt v1 contains original ID/kind, new batch/operation IDs, Reversal kind, original magnitude/currency, compensating DebitAccountId/CreditAccountId and new UTC/accounting metadata. Receipt monetary facts describe the persisted original; they are not stored as editable duplicates on the reversal row. There is no current balance in the receipt.

| Failure | Meaning and persistence |
| --- | --- |
| InvalidInput | Malformed ID/reason/key or forbidden SQL input shape; no reservation |
| NotFound | Missing or foreign original; rejected outcome/audit only |
| OperationNotReversible | Original is Opening or Reversal; rejected outcome/audit only |
| AlreadyReversed | Another complete reversal exists under the participant locks; rejected outcome/audit only |
| InsufficientFunds | Compensating debit would make a balance negative; rejected outcome/audit only |
| BalanceLimitExceeded | Compensating credit would exceed the financial bound; rejected outcome/audit only |
| IdempotencyConflict | Same owner/type/key with changed valid intent; original outcome remains unchanged |
| JournalInconsistent | Invalid/incomplete original or participant history, clock regression or aggregate corruption; rollback |
| TemporarilyUnavailable / CommitOutcomeUnknown | Existing bounded retry/recovery contract; never assume an unknown commit rolled back |

The two new Application failure values are appended to the existing enum. Historical wire/storage codes and receipt formats remain unchanged.

## Exact compensation and examples

| Original | Reversal effects |
| --- | --- |
| Income into A | Debit A by exactly the original magnitude |
| Expense from A | Credit A by exactly the original magnitude |
| Transfer A to B | Debit B and credit A by exactly the same original magnitude |

For an initial A=100, Income 20 leaves 120 and its reversal returns 100. Expense 20 leaves 80 and its reversal returns 100. With A=100/B=0, Transfer 20 leaves 80/20 and its reversal returns 100/0; pair wealth is conserved.

Every original remains present, with identical monetary fields, participants, reason, recording date, batch, receipt and audit. A reversal has a new ID and batch, required reason and current trusted date; it never changes an original status or adds a mutable reversed flag. Reporting can retain both periods rather than erase the original. No reporting endpoint is added here.

Only full compensation is supported, at most once per original. The referenced positive magnitude remains within 0.01 through 99999999999999999999999999.99 and its original DOP/USD currency. There are no fees, partial amounts, FX, cross-owner compensation or reversals of reversals.

Active and Archived participants both permit this exact compensation. They must retain valid initialized history and original ownership/currency. The writer never reactivates them or permits an ordinary operation through this exception. Ordinary Opening/Income/Expense/Transfer still reject Archived accounts.

Every final account balance must stay in [0, Bmax]. Income reversal can fail if its proceeds were spent. Expense reversal can fail if later income filled the headroom. Transfer reversal can fail if B spent its proceeds or A no longer has headroom. The entire reversal rejects in either case. Spending exactly the compensating debit's available balance is valid; reaching exactly Bmax is valid.

Current validated balances/magnitude use decimal(28,2); prospective arithmetic and pair totals use the existing exact decimal(29,2)/decimal(30,2) comparisons. Aggregation is range-checked before narrowing. No float, double, rounding or unchecked arithmetic on decimal(38,2) aggregates is introduced. Tests cover the minimum and maximum original magnitudes for all three kinds.

```csharp
var owner = new FinancialOwner(authenticatedUserId); // Authorized server context.
var request = new FinancialReversalRequest(originalOperationId,
    "Synthetic entry was recorded by mistake");
var result = await financialAccountService.RecordReversalAsync(owner, request,
    "reverse-demo-001", cancellationToken);

// Recovery uses the identical owner/type/key/intent, even after a lost response.
var replay = await financialAccountService.RecordReversalAsync(owner, request,
    "reverse-demo-001", cancellationToken);
```

## Shared SQL transaction and locks

dbo.RecordFinancialReversal exposes only owner, original ID, reason and key. It selects fixed CommandType=Reverse (6) and calls the existing private ExecuteFinancialWrite, which inserts journal Kind=Reversal (5). The private dispatcher rejects monetary/account/currency inputs for Reverse and rejects an original ID on ordinary commands.

1. Validate trusted owner and input shape. Begin one READ COMMITTED transaction with XACT_ABORT ON and a 5000ms lock timeout; ambient transactions are refused.
2. Acquire the existing exclusive transaction-owned owner/command/key application lock. Resolve matching replay or changed-intent conflict before reevaluating mutable conditions.
3. Read the owner-scoped original through a singleton PK seek. Validate eligible kind and complete compatible confirmation. This immutable original read adds no retained original U lock ahead of account anchors.
4. Derive compensating accounts/amount/currency. Income uses its destination as the debit; Expense uses its source as the credit; Transfer uses its destination as debit and source as credit.
5. Acquire the same Accounts U/HOLDLOCK anchors used by all existing financial commands/readers. The unchanged helper enumerates a private ID table in native SQL uniqueidentifier order and completes separate forced singleton PK seeks. Semantic debit/credit order does not change global lock order. An Accounts ORDER BY alone is not the guarantee.
6. Under those retained locks, verify owned participant facts and check for an existing reversal. A complete existing reversal produces AlreadyReversed; incomplete reversal history fails closed. Then read initialized complete derived balances and validate every compensating final balance and transfer conservation.
7. Capture one SQL UTC datetimeoffset(7) and the current America/Santo_Domingo accounting date through its verified Windows mapping. Check recording/effective-date regression against all participant history, including the original. Users cannot supply dates. Stored original dates remain untouched.
8. Append one batch, one reversal reference, persistent outcome/receipt and audit. The effects view derives one inverse effect for Income/Expense or two for Transfer. Re-read complete final balances under the same locks and require exact expected values/conservation before commit. Return the receipt after commit.

Business rejection commits only outcome/audit. Unexpected pre-commit errors roll back every new row and key reservation. The unique original-reference index complements the account-lock protocol; it is not used as the normal second-reversal error path. Two different keys targeting the same original share the same participant anchors, so the second request sees the first complete compensation and records a business rejection. The SQL race tests verify this for all three kinds.

Archive still uses the actual owner-scoped EF ExecuteUpdate path. Its X lock conflicts with the financial U anchor. Archive first and reversal first both permit exact compensation, leave the account archived and serialize correctly. No account setter, reactivation or separate archive lock API is introduced.

Account ordering reduces cycles but cannot exclude deadlocks from FK/index/history reads, missing-key ranges, escalation or privileged activity. Existing journal-index contention can serialize unrelated work. No isolation or TLS settings were changed, and no NOLOCK shortcut is used.

## Idempotency and recovery

The unique case-sensitive scope remains (owner, command type, key). Reverse=6 is independent of every earlier command. SQL hashes UTF-16LE without a BOM using SHA-256 and stores uppercase hexadecimal, FingerprintVersion=1:

```text
VF-REVERSE\n1\n{lowercase original Guid D}\n{reason UTF-16 length}:{trimmed reason}
```

Here \n means LF, with no extra delimiter after the reason. Internal Unicode/whitespace/case are preserved; no NFC normalization is inferred. Owner is scoped separately. Immutable original effects are resolved from the reference, not accepted into the payload. Generated IDs, current balances/status, recording dates and transient database results are excluded.

Matching retries return the stored receipt or rejection before inspecting current reversal/funds/status. An already successful request therefore replays success, while a new key on its original gets AlreadyReversed. A persisted insufficient-funds/headroom rejection remains rejected after capacity changes; a new key can deliberately reevaluate. Changed original or reason with the same key conflicts. Opening/Income/Expense/Transfer fingerprint bytes, versions, scopes and receipts remain compatible.

SqlFinancialJournal uses one shared retry pipeline for every write. Each complete attempt gets a fresh connection, with at most three attempts, 50/100ms backoff and 15-second command timeout. Native deadlock/lock-timeout and transient recovery classifications are unchanged. A retry never resumes at the remaining INSERT or changes the key. Exhausted ambiguous recovery returns CommitOutcomeUnknown; cancellation or response loss is not proof of rollback or exactly-once external delivery.

## Migration, permissions and test safety

Migration 20261124160033_FinancialReversalWriter alters ExecuteFinancialWrite, adds RecordFinancialReversal and grants only its EXECUTE permission to VaultFinanceFinancialRuntime. The [reviewed idempotent SQL](../backend/scripts/sql/FinancialReversalWriter.sql) changes no table/column/index/view/trigger and contains no destructive existing-schema/data operation. Previous migrations/resources are untouched. Down remains forward-only with an explicit refusal; use a separately reviewed corrective migration.

Private dispatcher/helpers stay ungranted; direct journal SELECT/INSERT/UPDATE/DELETE denials remain. Static dbo ownership chaining permits the fixed wrapper without runtime DDL rights. A non-owner WITHOUT LOGIN probe verifies the new entry point, direct-DML/private-dispatch denial and refusal of extra monetary parameters. A sysadmin/dbo or compromised trusted backend remains outside these protections; deployment must provision a suitably restricted service principal.

Only the existing VaultFinanceJournalTests was migrated after exact catalog/local-server/Windows-authentication/ownership-marker/known-schema/history checks. Database creation was disabled. All data is synthetic and isolated by fresh owner Guids; financial history is retained. Test fault/gate triggers and temporary principals are removed only after object ID/definition-marker or SID/catalog ownership checks. No database is reset, no history is purged and no secrets are logged.

From Vault-Finance, securely configure SqlTestConnection for the marked dedicated database and preserve approved transport security:

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
dotnet ef migrations script 20261124152847_FinancialTransferWriter 20261124160033_FinancialReversalWriter --idempotent --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build --output .local/FinancialReversalWriter.review.sql

$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    # The guarded fixture applies reviewed pending migrations only to the marked test database.
    dotnet test VaultFinance.slnx --no-build --no-restore --logger "trx;LogFilePrefix=reversals" --results-directory .local/test-results/reversals
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build -- --environment Development
```

Do not run an unguarded database update against DefaultConnection. API startup does not migrate; development promotion requires a separate reviewed target/safety process.

## Verified results and limits

Restore succeeds; build has zero warnings/errors. The full suite passes 207 unit and 398 integration cases, 605 total, with zero failures/skips. All 528 prior cases remain; 11 Application unit cases and 66 SQL cases were added. EF reports no pending model changes, and SQL migration/model alignment tests pass.

The new cases verify DOP/USD inverse effects and balances, complete original-history preservation, minimum/maximum magnitudes, funds/headroom rejection, archived participants without reactivation, missing/foreign/forbidden originals, second reversal, replay/conflict, durable rejected attempts/new keys, full Unicode fingerprint, scope separation, malformed SQL input, incompatible/incomplete original confirmation, clock regression, restricted permissions and full audit-failure rollback for all three kinds.

Concurrency uses independent SQL sessions with controlled native account locks/audit gates and DMV evidence of distinct blocked sessions before release. It covers different-key and identical-key reversals for each kind; reversal versus Expense, Income, same/opposite-direction Transfer; and both archive/reversal orders including both Transfer participants. Task.WhenAll or arbitrary delays alone are not considered proof. VIEW SERVER STATE is needed for this evidence; missing evidence fails the test.

Independent read-only before/after inspection confirms unchanged development object metadata, row counts and migration history: VaultFinanceDb retains InitialPersistence only. VaultFinanceJournalTests has six migrations and nine financial procedures; no temporary reversal triggers/principals remain. No development migration or write was performed.

Physical connection loss during commit, a deliberately forced native deadlock victim, exhaustive lock-escalation/query-plan stress and production multi-node deployment were not fault-injected. Retained native lock-timeout tests exercise the shared retry pipeline but do not certify these other scenarios. Opening correction, financial HTTP authorization/CSRF integration and deployment principal provisioning remain future increments. ADR acceptance and these tests are not a claim of absence of deadlocks or exactly-once response delivery.
