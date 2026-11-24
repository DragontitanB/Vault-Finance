# Atomic Opening Corrections

Status: Application and SQL implementation, validated only in the marked VaultFinanceJournalTests database. Updated: 2026-11-24.

This increment implements the existing correction policy in [ADR-002](decisions/ADR-002-financial-operation-journal.md) and the [engine design](financial-engine-design.md), extending the shared [opening](financial-opening-writer.md), [Income/Expense](financial-income-expense.md), [Transfer](financial-transfers.md) and [Reversal](financial-reversals.md) writer. No new architecture decision is introduced; ADR-002 remains Accepted and unchanged. This is simulated personal finance. No HTTP endpoint, frontend, mutable balance or general reconciliation service is added.

## Review and resolved policies

FinancialOpeningVersions already has a PK on (owner, currency, account, revision), a required predecessor for revisions after 1, the revision-minus-one CHECK, same-owner/currency FKs, and an optional typed Opening reference. Null OpeningOperationId explicitly means zero, not an absent initialization. All history tables remain append-only. This schema supports successive corrections without table changes.

Two policies were already approved and are applied explicitly:

- Requesting the current opening amount, including zero to zero, returns a stable OpeningUnchanged rejection. It creates no version, batch or monetary operation; only outcome/audit are persisted.
- Positive replacement requires Active status. The ADR permits exact cancellation on Archived accounts: positive to zero is allowed if the final balance remains valid, with a new zero revision in the same batch. Archived zero to zero follows the unchanged rule. Archived positive replacement rejects. No reactivation occurs.

These results follow the existing validation order: ownership, status/currency, initialization/integrity and expected revision precede the same-amount check. A stale same-amount request is a version conflict; a positive request on an Archived account is Archived even if its amount is unchanged.

The existing ordinary reversal command rejects Opening and Reversal targets. Correction alone selects the current Opening under the account lock and creates its internal compensation. Clients cannot select an obsolete opening or reverse internal compensations.

## Application contract

IFinancialAccountService.CorrectOpeningAsync accepts a separate authorized FinancialOwner, CorrectOpeningRequest, idempotency key and cancellation token. The request contains AccountId, desired Amount, Currency, ExpectedRevision and required Reason. It contains no owner, old amount, original operation ID, compensation fields, timestamp or accounting date.

Application validates a nonempty account ID, ExpectedRevision >= 1, DOP/USD normalization, decimal amount from zero through 99999999999999999999999999.99, at most two effective decimals, trimmed reason of 1-200 UTF-16 code units, and the existing 1-128 ASCII letter/digit/hyphen/underscore key format. Money and FinancialAmount are unchanged; zero replacement creates no FinancialAmount or zero monetary row. SQL independently validates wide textual inputs before narrowing.

ExpectedRevision comes from the caller's previously received opening/correction receipt. It is an optimistic expectation checked under the SQL account anchor, not a replacement for that lock. A stale/future expectation returns OpeningVersionConflict without revealing another owner's account/version. Missing and foreign accounts return the same NotFound.

OpeningCorrectionReceipt v1 contains account/batch IDs, previous and new revisions, previous opening ID, optional compensation/replacement IDs, previous/new opening amounts, currency and current UTC/accounting metadata. These are immutable historical facts; no current balance is included. Replay returns that receipt even after later revisions or archive.

```csharp
var owner = new FinancialOwner(authenticatedUserId); // Authorized server context.
var request = new CorrectOpeningRequest(accountId, 80m, "DOP", ExpectedRevision: 1,
    Reason: "Synthetic initial position was overstated");
var corrected = await financialAccountService.CorrectOpeningAsync(owner, request,
    "opening-correction-001", cancellationToken);
```

The caller must resolve owner from authorized server state; constructing FinancialOwner alone is not authentication. No client HTTP binding is implemented here.

## Versions, components and history

The current revision is the highest persisted revision for the owned account/currency, read under its retained account lock. PK/predecessor FK/checks enforce one contiguous chain and forbid duplicate roots/revisions. The writer also validates each version's batch command, reference, required compensation, exact component count and absence of an already-compensated current opening. Inconsistent history fails closed rather than being treated as a new zero opening.

| Current opening | Requested opening | One atomic batch |
| --- | --- | --- |
| Positive | Different positive | Reversal of current Opening + replacement Opening + next version + outcome/audit |
| Positive | Zero | Reversal of current Opening + next zero version + outcome/audit; no replacement monetary row |
| Zero | Positive | Replacement Opening + next version + outcome/audit; no fake zero reversal |
| Zero or positive | Same amount | Rejected outcome/audit only |
| Uninitialized | Any valid amount | Uninitialized rejection; use InitializeOpening instead |

The compensation is a Kind=Reversal reference with null amount/participant fields, derived exclusively from the current persisted Opening. A replacement is Kind=Opening with the account's currency and requested positive amount. Both share CommandType=CorrectOpening (2), batch and timestamp. No monetary operation represents zero. Original opening rows, all old versions and subsequent Income/Expense/Transfer/Reversal rows and their receipts/audit remain unchanged.

Success advances revision by exactly one and links PreviousRevision to the checked current revision. Native int revision capacity is checked before increment; an exhausted current int.MaxValue returns OpeningRevisionLimitExceeded rather than overflowing. That physical capacity boundary is guarded but not populated with billions of test revisions. Normal stale requests, including an expected int.MaxValue against a small current revision, return version conflict.

## Final-batch arithmetic and examples

The final balance contract is:

```text
final balance = current derived balance - current opening amount + requested opening amount
```

Validate only that complete final result against [0, Bmax]. Current validated operands use decimal(28,2); subtraction produces decimal(29,2) and subsequent addition decimal(30,2). The result fits the widened temporary and is bounded before publication. There is no arithmetic on unchecked decimal(38,2) aggregates and no rounding/float/double. The existing reader range-checks its aggregate before narrowing.

For opening 100 and later expense 80, the current balance is 20. Correcting opening to 80 appends -100 and +80 in one transaction: final zero is valid, even though the uncommitted compensation prefix would total -80. Correcting to 70 rejects with InsufficientFunds and leaves the original opening unconsumed. Correcting an explicit zero opening to 1 when the balance is already Bmax rejects with BalanceLimitExceeded.

Successive opening values 100 -> 80 -> 0 -> 20 preserve every opening and compensation, with revisions 1 -> 2 -> 3 -> 4. Each correction targets the latest version only. A replay of the first correction still returns revision 2, not revision 4. A new request based on revision 1 receives OpeningVersionConflict.

## Shared transaction and lock protocol

The public dbo.CorrectFinancialOpening wrapper selects fixed command type 2. It exposes only trusted owner, account, desired amount/currency, expected revision, reason and key. The existing private ExecuteFinancialWrite owns the complete READ COMMITTED transaction with XACT_ABORT ON, 5000ms native lock timeout and no ambient transaction/external call.

1. Validate input and trusted owner. Acquire the transaction-owned exclusive owner/type/key application lock; resolve authorized replay/conflict before reevaluating mutable version/status/funds.
2. Acquire the existing owner-scoped Accounts U/HOLDLOCK anchor through the unchanged helper. For all financial operations the helper enumerates a private ID set in native SQL Guid order and completes separate forced singleton PK seeks; Accounts ORDER BY alone is not the locking guarantee.
3. Re-read ownership, currency and status, enforcing the archived cancellation exception only for target zero. Read the initialized complete derived balance.
4. Call private ReadLockedFinancialOpening under that anchor to validate the chain and load its current revision/opening/magnitude. Check expected revision, unchanged amount and increment capacity. No client historical amount is trusted.
5. Compute and validate the complete final balance. Capture one trusted SQL UTC datetimeoffset(7) and current America/Santo_Domingo date, rejecting clock/date regression against relevant account history. Original dates remain intact; no backdating/scheduling input exists.
6. Append batch, optional compensation, optional replacement, next version, immutable outcome/receipt and audit. Do not call the balance validator between the component inserts.
7. Re-read the complete final derived balance and opening chain; require exact expected balance, revision, replacement and component contents. Commit everything together, then return the stored receipt.

Business rejections persist only rejected outcome/audit. Unexpected failures roll back batch, components, version, result and audit together, leaving the key available. PK/FKs, the unique original-reversal index, scoped idempotency uniqueness and append-only guards complement the writer; none alone proves the full protocol.

ReadLockedFinancialOpening is account-specific write validation, not a general reconciliation service. It is private and requires a committable transaction plus its caller's account anchor. Existing ordinary commands and derived-balance reads retain their prior behavior and contracts.

## Concurrency, archive and visibility

Two distinct keys based on the same revision serialize at the account anchor. Exactly one can append its successor; the second reads the new current revision and records OpeningVersionConflict. Identical simultaneous keys serialize at the request lock and replay one receipt. Changed valid payload with one key conflicts without reevaluating the version.

Income, Expense, Transfer, Reversal and derived reads use the same account anchors. Transfers retain global two-account ordering. Archive's actual EF ExecuteUpdate obtains a conflicting X lock. If archive commits first, positive replacement rejects, while exact cancellation to zero can still proceed under the approved exception and final bounds. If correction commits first, archive follows; history remains and the account is never reactivated.

The public balance reader takes the same U anchor, so it waits through compensation/replacement and cannot publish the negative prefix. A controlled SQL gate after compensation verifies this with separate writer/reader sessions. Direct runtime reads of the structural effects view remain denied. Administrative dirty reads are outside this published read contract; no NOLOCK or isolation change is introduced.

Ordered anchors reduce cycles but do not eliminate deadlocks from history/index/FK locks, escalation or administrative activity. Previously observed journal-index contention can serialize unrelated requests. Existing whole-protocol retries remain bounded to three fresh connections with 50/100ms backoff and a 15-second command timeout. Ambiguous commit recovery must reuse identical owner/type/key/payload; exhausted ambiguity returns CommitOutcomeUnknown, not a rollback claim. No exactly-once response-delivery guarantee is made.

## Canonical idempotency and failures

Scope remains (owner, server-selected CorrectOpening=2, case-sensitive key). SQL hashes UTF-16LE bytes without BOM with SHA-256, stored as uppercase hexadecimal, FingerprintVersion=1:

```text
VF-CORRECT-OPEN\n1\n{lowercase account Guid D}\n{expected revision base-10}\n{amount exactly two decimals}\n{uppercase currency}\n{reason UTF-16 length}:{trimmed reason}
```

Here \n denotes LF. There is no delimiter after the reason; internal Unicode/case/whitespace are preserved. No NFC normalization is inferred. Owner is scoped separately; generated IDs, resolved old opening amount, current balances/version and timestamps are excluded. The expected revision is part of intent, not mutable lookup output.

Historical Opening/Income/Expense/Transfer/Reversal encodings, versions, receipts and scopes remain unchanged. A matching rejected no-change/version/funds/headroom outcome remains stable even when conditions change. An intentional reevaluation uses a new key and an appropriate expected revision. Changed account, desired amount, currency, expected revision or reason with the same key conflicts. Technical failure before commit does not consume the key. Outcomes/audit remain retained for the MVP lifetime.

The new failure values are OpeningVersionConflict, OpeningUnchanged and OpeningRevisionLimitExceeded, appended to the existing Application enum. Other failures reuse NotFound, Archived, CurrencyMismatch, Uninitialized, InvalidInput, InsufficientFunds, BalanceLimitExceeded and JournalInconsistent. No HTTP status mapping is introduced by this increment.

## Migration, permissions and reproduction

20261124162754_FinancialOpeningCorrectionWriter adds ReadLockedFinancialOpening, alters ExecuteFinancialWrite and adds CorrectFinancialOpening plus its EXECUTE grant. It changes no table, column, index, constraint, view or trigger. Previous migrations/resources remain untouched. The [reviewed idempotent SQL](../backend/scripts/sql/FinancialOpeningCorrectionWriter.sql) has no destructive existing-schema/data operation. Down explicitly refuses history destruction; use a separately reviewed forward corrective migration.

VaultFinanceFinancialRuntime gains only EXECUTE on CorrectFinancialOpening. The private helper/dispatcher remain ungranted, and direct journal reads/inserts/updates/deletes remain denied. Static dbo ownership chaining supplies internal access without DDL grants or arbitrary historical-effect parameters. Non-owner runtime tests verify SQL 229 denials and refusal of an extra historical-amount parameter. Production must use a properly restricted principal; a development sysadmin/dbo or compromised trusted backend remains outside these guarantees.

Only the existing marked VaultFinanceJournalTests is migrated, after exact catalog/local-server/Windows-authentication/ownership-marker/known-schema/history checks. Creation is disabled, and no financial history is deleted or reset. API startup does not migrate. VaultFinanceDb remains at InitialPersistence.

From Vault-Finance, configure the dedicated SqlTestConnection securely without logging its value and preserve approved TLS/certificate settings:

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
dotnet ef migrations script 20261124160033_FinancialReversalWriter 20261124162754_FinancialOpeningCorrectionWriter --idempotent --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build --output .local/FinancialOpeningCorrectionWriter.review.sql

$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    # The guarded test fixture alone applies reviewed pending migrations to its marked target.
    dotnet test VaultFinance.slnx --no-build --no-restore --logger "trx;LogFilePrefix=corrections" --results-directory .local/test-results/corrections
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build -- --environment Development
```

Do not run an unguarded database update against DefaultConnection. Promotion to development requires a separate reviewed target/safety procedure.

## Test evidence and limits

Final validation: restore succeeds; build reports zero warnings/errors; 228 unit and 468 integration cases pass, 696 total, with zero failures/skips. All 605 prior cases remain; 21 Application unit and 70 real SQL cases were added. EF reports no pending model changes and the migration/model alignment tests pass. The final TRX artifacts are under .local/test-results/corrections-final-serialized.

Independent read-only before/after inspection confirms identical development object metadata, row counts and migration history: VaultFinanceDb retains InitialPersistence only. The dedicated test database has seven migrations and eleven financial procedures. No test-owned correction trigger or temporary principal remains after cleanup. No development write or migration was performed.

The new cases cover positive/zero transitions, same-amount rejection, successive complete chains, preservation of all earlier records, final-only funds/headroom checks, later ordinary activity/reversals, ownership/state/currency/input failures, expected-version conflicts, historical replay, payload fingerprints, durable rejections/new keys, internal-component reversal rejection, malformed history/clock regression and restricted execution.

Independent actual SQL sessions, held native locks, test-owned gates and DMV observations verify same-version/identical/changed-payload races, mixed writers, both archive orders and blocked balance visibility through a negative compensation prefix. Task.WhenAll or timing alone is not evidence. VIEW SERVER STATE is required; missing evidence fails these tests. Independent canonical-row/reference calculations verify balances separately from the production effects view.

Fault triggers after compensation, replacement and audit verify full rollback and successful same-key retry. Cleanup requires the generated object ID/definition marker or principal SID/catalog to match; synthetic immutable data is retained. Financial test classes now share a nonparallel xUnit collection because their temporary triggers share SQL metadata. This prevents concurrent test DDL, while each concurrency case still creates independent contending SQL sessions. The first full run exposed a concurrent trigger-creation error concerning inserted; fixture scheduling was corrected rather than weakening the financial protocol or adding a production DDL retry.

Physical network loss during commit, a deliberately forced native deadlock victim, exhaustive escalation/plan stress, actual exhaustion of the int revision space and production multi-node load are not fault-injected. Retained native timeout tests cover the shared retry pipeline, not those other scenarios. No general reconciliation engine, financial HTTP authorization/CSRF workflow or restricted production-principal deployment is delivered here.
