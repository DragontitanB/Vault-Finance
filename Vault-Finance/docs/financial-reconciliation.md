# Financial Journal Reconciliation

Status: implemented, read-only Application use case and SQL capture adapter. Updated: 2026-11-24. This inspects simulated finance assertions, not bank funds or settlement. It introduces no HTTP endpoint, scheduled job, repair, or financial command. ADR-002 remains Accepted without amendment.

## Contract and scope

`IFinancialInspectionService.InspectAsync(FinancialInspectionContext, CancellationToken)` inspects the complete retained history of exactly one explicitly authorized owner, including uninitialized and archived accounts. It does not accept a partial account list: transfers, reversal references and opening chains must be available together. An empty owner history is a valid empty scope, not proof that a user exists.

The trusted context contains ActorId, OwnerId and CanInspectJournal. An administrative caller must establish that capability and owner scope through its authorization policy before constructing the context. These values are not client DTO fields. Ordinary contexts return Forbidden without calling the reader. No HTTP or ordinary-user path constructs an authorized inspection context in this increment.

The report contains owner scope, InspectionId, CapturedAtUtc, ConsistencyMode, inspected account identifiers with explicit initialization flags, and findings with stable codes and optional account/batch/operation/outcome/revision identifiers. Amounts, account names, emails, reasons, idempotency keys, fingerprints, receipt JSON, audit JSON and SQL exception messages are excluded. Raw `JournalSnapshot` records are internal diagnostic input contracts; never serialize or log them for an ordinary user.

| Status | Meaning |
| --- | --- |
| Healthy | Complete capture, all supported checks completed, no finding. This is bounded assurance, not proof against an administrator rewriting coherent history. |
| Discrepancies | A complete supported inspection found structural, monetary or contract discrepancies. No repair is attempted. |
| Incomplete | Row/finding limit or an unsupported historical contract prevents full inspection. Existing findings can still be useful; never display Healthy. |
| Forbidden | Trusted caller capability absent; no database access. |
| ExecutionFailed | Invalid context/scope, permission failure, cancellation, unavailable calendar, read failure, incomplete result stream or lock/command failure. No successful capture is claimed. |

Unknown fingerprint/receipt versions and unknown business rejection codes produce Incomplete, rather than silently accepting them or declaring legitimate future formats corrupt. Current stored contracts use version 1. V1 receipts are checked semantically, tolerate additional compatible fields, and are compared to their original immutable batch/version, not today's opening head or balance.

## Independent algorithm

`JournalIntegrityEvaluator` is pure Application code. It does not invoke the SQL effects view, SQL balance arithmetic, `SqlFinancialJournal`, or writer validators to reconstruct balances. The SQL adapter separately captures canonical facts, the existing view and results of the unchanged usual balance validator as comparators.

1. Reject incomplete or cross-owner input before exposing row identifiers. Check identities, duplicates, currencies, operation shapes, references, recording UTC offsets and America/Santo_Domingo effective dates.
2. Decode decimal coefficients directly into `BigInteger` cents. Require effective precision of at most two fractional digits and positive canonical monetary magnitudes within FinancialAmount.MaximumValue (`99999999999999999999999999.99`). Accumulation cannot overflow CLR decimal, even for invalid large histories. No floating point, rounding, exchange rates or cross-currency summation is used.
3. Interpret canonical operations independently: Opening/Income credits its destination, Expense debits its source, and Transfer debits its source and credits its destination by the exact same magnitude. A Reversal obtains the persisted original and negates those independently reconstructed effects. It must match original kind/owner/currency and causal order, and cannot reverse another Reversal.
4. Compare each independently expected effect with the view row, including exact cents, participant, currency, batch, sequence, kind and dates; check missing/duplicate effects. Transfer and Transfer-reversal projected vectors must sum to zero. Equal final totals alone cannot hide an incorrect compensating effect.
5. Verify each complete opening chain: root revision 1, contiguous successors, previous-revision links, no forks/duplicates/reused batches, positive Opening references and exact successor compensation. A positive current version must not already be compensated. Each Opening operation must belong to exactly one version.
6. Check exact batch composition. Initialization may contain zero or one monetary Opening. Correction contains precisely the previous positive-opening compensation and/or new positive Opening dictated by its version pair. Income, Expense and Transfer have one canonical operation; ordinary Reversal has one inverse of Income/Expense/Transfer. An Opening inverse is valid only inside the corresponding correction batch.
7. Replay complete batch deltas per account in Sequence order, checking initialized roots, calendar progression, nonnegative balance and maximum bound at every committed batch boundary. Sequence supplies per-account writer order; it is not a global commit watermark. Compare final exact cents and initialization with the usual balance result captured in the same freeze.
8. Check one posted outcome per batch and one audit per persisted outcome, scoped owner/command/key uniqueness, timestamps, action, audit summary, result kind/code, receipt fields and supported fingerprint versions. For successful commands reconstruct the v1 UTF-16LE canonical payload independently from immutable rows and compare SHA-256. Rejected requests have no batch or monetary operation; their original payload is not retained, so only hash format/scope, rejection receipt and audit contracts can be checked.

An explicit zero opening has a version with no OpeningOperationId, and is initialized at zero. No version means uninitialized with null reported amount. A successful zero opening/cancellation must still have a batch, result and audit. An unchanged opening correction, including zero-to-zero, is a persisted business rejection with no new batch/version.

Example: Opening 100, Expense 80, correction 100→80 reconstructs committed balances 100, 20, 0. The correction contributes -100 +80 as one delta; its temporary -80 prefix is never tested as a committed balance. Multiple corrections preserve and validate every earlier receipt/version.

The accepted archive exception remains exact cancellation of a positive current opening to zero, provided the final balance stays valid. Positive replacement ordinarily requires Active status. Exact ordinary reversals may compensate archived accounts. Current Accounts has no archive timestamp or historical state log; an account archived today may legitimately have older Income/Expense/Transfer. The evaluator therefore does not infer that historical writes violated archive policy from its current status. Writer tests establish the live policy; reconstruction cannot recover missing historical state evidence.

## Coherent capture and locking proof

`dbo.CaptureFinancialJournalInspection` owns a SERIALIZABLE read-only transaction and rejects ambient transactions. It takes actual shared table locks using separate `TABLOCK,HOLDLOCK` reads, in this order: Accounts, FinancialPostingBatches, FinancialOperations, FinancialOpeningVersions, FinancialRequestOutcomes, FinancialAuditEntries. Early acquisition counts are discarded. No payload is exported until all six locks are held; counts and all output queries then run under those retained locks.

These S table locks conflict with the IX/X resources required for DML and with writer account update anchors. Consequently an in-flight writer either completes before the freeze or waits until its end. Committed rejections without account writes are also covered because outcomes/audits are locked. Archive and account creation are covered by the Accounts lock. A transaction spanning several tables cannot leave an externally visible partial commit between these reads. If lock acquisition encounters a cycle, timeout or deadlock, capture fails; it does not compare fragments from different instants. The mode is `shared-table-locks-v1`.

After the last lock, the procedure records a new inspection identifier and UTC time. Canonical rows, view effects and the unchanged `ReadLockedFinancialBalance` results all belong to that frozen state. The helper ordinarily relies on a retained account anchor; here the six shared table locks exclude all relevant DML more broadly. The helper remains unchanged and is a comparator, never the independent reconstruction. Known helper integrity/overflow rejections become `balance.reader_rejected`; unexpected errors invalidate capture. Only this read-only procedure uses XACT_ABORT OFF to catch those known validation errors in a committable read transaction; writer XACT_ABORT ON remains unchanged.

The SQL procedure commits before emitting its final completion marker. The adapter requires that marker, matching inspection/owner/row counts, every result set and end-of-stream. A timeout, cancellation, broken stream or SQL exception discards partial data. Connections are independent, not enlisted in writer transactions. Pure evaluation occurs after capture releases locks.

This is a locking snapshot, not SQL SNAPSHOT isolation. No ALLOW_SNAPSHOT_ISOLATION, READ_COMMITTED_SNAPSHOT or instance option is changed. It works on SQL Server 2019 Express without row-versioning setup. The locking basis is documented in Microsoft's [table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver15) and [transaction locking guide](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver15). NOLOCK and dirty reads are not used.

Cost is deliberately coarse: six shared table locks block relevant writers for **all owners**, even though returned facts are owner-scoped. Empty scopes also acquire the locks. Lock wait is bounded at 5 seconds per wait; the adapter's command timeout is 20 seconds. Those are not a hard end-to-end runtime SLA. A slow result consumer can prolong the SQL transaction until timeout/cancellation/disposal; the adapter buffers bounded results promptly, then evaluates offline. Row limit is 20,000 across canonical rows, effects and ordinary-balance rows, and findings are capped at 1,000 with Incomplete on truncation. Limits do not bound the work of acquiring/counting/scanning rows. Several evaluator checks perform repeated scans with worst-case quadratic cost; this is small-portfolio inspection, not a large-ledger analytics or throughput design. Histories exceeding the cap fail explicitly. Consistent incremental/chunked inspection needs a separately reviewed snapshot/watermark protocol; independent paged reads would be unsafe.

Deadlocks and shared-index contention remain possible; the reader returns `inspection.unavailable` on native timeout/deadlock. It performs no hidden retry. A caller may repeat the whole read later. No absence-of-deadlocks guarantee is made.

## Responsibility boundaries

| Invariant | Existing SQL structure | Writer protocol | Independent inspection |
| --- | --- | --- | --- |
| Amount precision, supported currencies, operation shapes | decimal(28,2), CHECKs | validates input before SQL coercion | revalidates observed facts and cents |
| Owner/currency references, identities | typed/composite FKs and keys | trusted authorization and locked lookups | scope/ref/participant checks |
| No history mutation | append-only triggers, restricted role | append-only commands | cannot prove an earlier row was never rewritten by an administrator |
| One inverse per original | unique original-reference index | locks and business result | duplicate/reference/causal and exact inverse checks |
| Opening chain | PK/previous-revision FK and uniqueness | current revision, compensation, final balance | full chain and complete-batch reconstruction |
| No overdrafts/overflow, exact transfer | structural safeguards are insufficient alone | account anchors and final vector validation | committed per-batch balance bounds and conservation |
| Complete result/audit/batch | references/uniqueness do not prove every required row exists | one atomic commit and canonical receipts | exact membership and supported contracts |
| Idempotent behavior | scoped unique key | fingerprint/replay/conflict/rejection handling | stored consistency; cannot rerun a missing rejected payload |

## Stable finding and error codes

Identifiers supply minimal diagnostic context. Multiple findings can arise from one broken reference. Codes are contract values; consumers must not parse translated text.

| Codes | Meaning |
| --- | --- |
| `account.duplicate`, `account.invalid` | Duplicate identity or invalid current metadata. |
| `operation.duplicate`, `operation.shape_invalid`, `operation.batch_missing`, `operation.participant_invalid` | Invalid canonical movement/participant/batch. |
| `reversal.reference_invalid`, `reversal.causality_invalid`, `reversal.duplicate` | Incorrect inverse reference/order or repeated inverse. |
| `effect.mismatch`, `effect.missing_or_duplicate`, `transfer.conservation_invalid` | Projection disagrees with canonical semantics or fails conservation. |
| `opening.duplicate_revision`, `opening.fork`, `opening.chain_invalid`, `opening.causality_invalid` | Broken revision sequence or ordering. |
| `opening.reference_invalid`, `opening.batch_invalid`, `opening.batch_reused`, `opening.compensation_invalid`, `opening.current_compensated`, `opening.operation_unversioned`, `opening.unchanged_revision`, `opening.initialization_missing` | Incorrect opening/component/version relationship or missing initialization. |
| `batch.duplicate`, `batch.invalid`, `batch.sequence_invalid`, `batch.composition_invalid`, `batch.outcome_missing_or_duplicate` | Invalid batch identity, kind, order or membership. |
| `calendar.invalid`, `calendar.regression` | UTC/accounting-date contract violation or per-account clock/date regression. |
| `balance.negative`, `balance.limit_exceeded`, `balance.mismatch`, `balance.reader_rejected`, `balance.report_missing_or_duplicate`, `balance.report_unknown_account` | Independent bound failure or usual-reader disagreement/failure. |
| `idempotency.duplicate`, `idempotency.duplicate_scope`, `idempotency.metadata_invalid`, `idempotency.membership_invalid`, `idempotency.posted_invalid`, `idempotency.rejection_invalid`, `idempotency.receipt_invalid`, `idempotency.fingerprint_mismatch` | Stored result, receipt or canonical payload inconsistency. |
| `audit.duplicate`, `audit.membership_invalid`, `audit.orphan`, `audit.contract_invalid`, `audit.summary_invalid` | Missing/duplicate/orphan audit or action/time/summary disagreement. |
| `idempotency.receipt_version_unsupported`, `idempotency.fingerprint_version_unsupported`, `idempotency.result_unsupported` | Unsupported contract; report is Incomplete. |

Execution/coverage errors are separate: `inspection.forbidden`, `inspection.invalid_context`, `inspection.scope_invalid`, `inspection.permission_denied`, `inspection.unavailable`, `inspection.cancelled`, `inspection.calendar_unavailable`, `inspection.read_failed`, `inspection.capture_incomplete`, `inspection.limit_exceeded`, `inspection.contract_unsupported_or_truncated`. No raw SQL message is returned.

## Permissions and deployment

New SQL role `VaultFinanceJournalDiagnostics` grants EXECUTE only on CaptureFinancialJournalInspection. It explicitly denies direct SELECT/INSERT/UPDATE/DELETE on Accounts and the five journal tables, SELECT on the effects view, and EXECUTE on all six mutators, the writer dispatcher and private lock/opening/balance helpers. Static dbo ownership chaining permits the procedure's fixed owner-filtered reads and private read validator. There is no dynamic SQL in the runtime capture.

The existing financial runtime role receives no inspection grant. Tests also give a temporary diagnostic principal runtime membership: the explicit diagnostic DENYs keep it read-only. Provision a separate operational identity; do not add diagnostic membership to the application's ordinary runtime principal because those DENYs would also disable financial commands. Principals with dbo/sysadmin privileges bypass this boundary and are outside the guarantee.

SQL accepts an owner parameter from a trusted diagnostic operator; it does not implement per-end-user row-level security. Anyone granted this privileged EXECUTE capability can request another owner explicitly. It must therefore remain restricted to approved operations staff/service identity. Application authorization establishes the requested scope; every returned canonical/projection row is owner-filtered and rechecked by the evaluator. There is no global scan/report exposed to users. ActorId is authorization context, not a new persisted financial audit: read-only inspection writes no audit or result. A future administrative host must implement its access policy and safe operational access logging separately.

Infrastructure registers the service/reader but maps no routes. An authorized internal host can resolve IFinancialInspectionService and call:

```csharp
// These values come from a trusted administrative policy, never a request body.
var context = new FinancialInspectionContext(operatorId, authorizedOwnerId, CanInspectJournal: true);
FinancialInspectionReport report = await inspection.InspectAsync(context, cancellationToken);
// Only Healthy means every supported check completed without findings.
```

Production DI still guards DefaultConnection to VaultFinanceDb, which deliberately lacks the financial migrations. Do not run this use case there yet. Tests construct the adapter with the separately validated dedicated context. Deployment of the full journal and a separate diagnostic principal/host remains an explicit future step; no credential is installed or committed by this increment.

## Migration and reproducible validation

Migration `20261124181610_FinancialJournalInspection` creates only the fixed capture procedure, diagnostic role and permissions. EF model/snapshot and existing tables, view, writer procedures and prior migrations are unchanged. The reviewed script is `backend/scripts/sql/FinancialJournalInspection.sql`; migration-history INSERT is EF bookkeeping, not inspection behavior. No DROP TABLE/COLUMN or destructive existing-schema operation is included. Down is explicitly unsupported; use a reviewed forward migration.

Only the existing marked VaultFinanceJournalTests database is migrated. Configure ConnectionStrings:SqlTestConnection securely as described in [journal persistence](financial-journal-persistence.md); it must be the dedicated local catalog with the existing project marker. No real connection string belongs in tracked code or command output. With that existing target:

```powershell
dotnet restore VaultFinance.slnx
dotnet build VaultFinance.slnx --no-restore
$env:VAULTFINANCE_SQL_TESTS = '1'
$env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = '0'
try {
    # Fixture verifies catalog, server, ownership marker and schema before migration.
    dotnet test VaultFinance.slnx --no-build --no-restore
} finally {
    $env:VAULTFINANCE_SQL_TESTS = $null
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = $null
}
dotnet ef migrations has-pending-model-changes --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --no-build
```

For a focused inspection run, append `--filter FullyQualifiedName~FinancialInspectionSqlServerTests` to the IntegrationTests project test command. No startup auto-migration is added. Do not use an unguarded database-update command against DefaultConnection.

## Evidence and limits

New pure fixtures represent invalid states without SQL corruption: projection/reported-balance mismatches, missing/duplicated effects, incorrect compensations, conservation failures, revision gaps/forks, incoherent batch membership, duplicate/wrong reversals, audit/result/fingerprint/receipt discrepancies, negative/oversized balances, unknown contracts and cross-owner input. They also check valid rejected requests and historical v1 receipts. These demonstrate evaluator detection, not bypass of actual SQL constraints.

New real SQL tests insert only valid synthetic histories under fresh owners: uninitialized/zero/positive/max openings, Income/Expense, Transfer and all three reversals, multiple corrections including negative internal prefix, archived cancellation and persisted rejections. Owner-row hashes across Accounts and all five journal tables stay identical before/after inspection, including no added audit. Separate principals prove ordinary runtime cannot inspect, diagnostic reads complete under denied direct access, and direct DML/mutator execution is denied.

Eight controlled concurrency cases cover both orders for Income, Expense, Transfer and opening correction. Reader-first cases gate the final audit-table lock, observe five retained OBJECT S locks in sys.dm_tran_locks, then observe a distinct writer blocked until capture proceeds. The report contains the complete pre-write state. Writer-first cases use a test-owned temporary trigger/applock gate, pause inside the writer (including immediately after correction compensation), observe an independent reader blocked, and then verify a complete post-commit Healthy report. Polling checks actual DMV contention with a deadline; Task.WhenAll only joins after synchronization. Tests require administrator visibility for DMV observations, not additional runtime grants.

Test trigger names, object identifiers, database and definition markers are checked before cleanup, and the existing nonparallel financial collection prevents interference with other classes' temporary triggers. Committed synthetic history is retained; no shared history is cleared. Cancellation, lock timeout and row-limit failures are explicitly tested as nonhealthy results.

This does not prove native-deadlock/network-loss fault injection, high-volume/production performance, every possible malicious corruption, cryptographic tamper evidence, actual historical archive state, missing rejected payloads, an operator's business intent, or bank reconciliation. A privileged actor could rewrite mutually consistent rows and evade semantic checks. External authoritative statements/checkpoints would be a separate design. Concurrent Reversal/archive interactions are covered by the existing writer protocol and its retained tests; the new capture gates specifically exercise Income/Expense/Transfer/correction.

Final validation: restore succeeded; build completed with zero warnings/errors; all 286 unit and 491 integration cases passed, with zero failures and zero skips (777 total). This retains all 696 earlier cases and adds 58 pure Application cases plus 23 real SQL cases. EF reports no pending model changes. The isolated database contains eight migrations through FinancialJournalInspection and 12 procedures; no temporary inspection trigger/principal remains. Independent read-only before/after queries confirm identical development object metadata, row counts and InitialPersistence-only history. This is metadata/count comparison, not an assertion of a full external forensic data checksum.

The first attempted migration failed because an unquoted RowCount alias conflicted with SQL syntax; it rolled back completely and its history remained at seven entries. The unapplied resource was corrected before successful application. The first executable SQL run then passed 22/23 cases, exposing SqlClient's SQL-exception form of cancellation; mapping now checks the cancellation token before classifying SQL errors. A temporary unit-test syntax error was corrected before the final clean build. These failures are resolved in the reported full run. No TLS relaxation was used. No deliberate corruption is inserted into either development or the shared test database by this increment.
