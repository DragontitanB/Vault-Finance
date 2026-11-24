namespace VaultFinance.Application.FinancialInspection;

// Trusted server authorization, never a client DTO. SQL also requires a separate diagnostic role.
public sealed record FinancialInspectionContext(Guid ActorId, Guid OwnerId, bool CanInspectJournal);

public enum InspectionStatus { Healthy, Discrepancies, Incomplete, Forbidden, ExecutionFailed }
public sealed record InspectionFinding(string Code, Guid? AccountId = null, Guid? BatchId = null,
    Guid? OperationId = null, Guid? OutcomeId = null, int? Revision = null);
public sealed record InspectedAccount(Guid AccountId, bool IsInitialized);
public sealed record FinancialInspectionReport(InspectionStatus Status, Guid OwnerId, Guid? InspectionId,
    DateTimeOffset? CapturedAtUtc, string? ConsistencyMode, IReadOnlyList<InspectedAccount> Accounts,
    IReadOnlyList<InspectionFinding> Findings, string? ErrorCode = null);

public interface IFinancialInspectionService
{
    Task<FinancialInspectionReport> InspectAsync(FinancialInspectionContext context, CancellationToken cancellationToken = default);
}

public interface IJournalInspectionReader
{
    Task<JournalCapture> CaptureAsync(Guid ownerId, CancellationToken cancellationToken);
}

public sealed record JournalCapture(JournalSnapshot? Snapshot = null, string? ErrorCode = null);

// Diagnostic input facts only. These records can represent invalid synthetic states without changing Domain.
// Receipts, reasons and fingerprints stay inside the inspection and are never copied to a public report.
public sealed record JournalAccount(Guid Id, Guid UserId, string Currency, int Status);
public sealed record JournalBatch(Guid Id, Guid UserId, long Sequence, int CommandType,
    DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate, string AccountingTimeZone);
public sealed record JournalOperation(Guid Id, Guid UserId, Guid BatchId, int Kind, string Currency, decimal? Amount,
    Guid? SourceAccountId, Guid? DestinationAccountId, Guid? OriginalOperationId, int? OriginalOperationKind, string? Reason);
public sealed record JournalOpening(Guid UserId, string Currency, Guid AccountId, int Revision, int? PreviousRevision,
    Guid BatchId, Guid? OpeningOperationId, int? OpeningOperationKind, string Reason);
public sealed record JournalOutcome(Guid Id, Guid UserId, int CommandType, string IdempotencyKey, string PayloadFingerprint,
    int FingerprintVersion, int OutcomeKind, Guid? BatchId, string ResultCode, string ReceiptJson, DateTimeOffset RecordedAtUtc);
public sealed record JournalAudit(Guid Id, Guid UserId, Guid OutcomeId, DateTimeOffset RecordedAtUtc, string Action, string SummaryJson);
public sealed record JournalEffect(Guid OperationId, Guid UserId, Guid AccountId, string Currency, Guid BatchId,
    long BatchSequence, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate, int Kind, decimal SignedAmount);
public sealed record JournalReportedBalance(Guid AccountId, string Currency, bool? IsInitialized, decimal? Amount, string? FailureCode);

public sealed record JournalSnapshot(Guid InspectionId, Guid OwnerId, DateTimeOffset CapturedAtUtc, string ConsistencyMode,
    bool IsComplete, IReadOnlyList<JournalAccount> Accounts, IReadOnlyList<JournalBatch> Batches,
    IReadOnlyList<JournalOperation> Operations, IReadOnlyList<JournalOpening> Openings,
    IReadOnlyList<JournalOutcome> Outcomes, IReadOnlyList<JournalAudit> Audits,
    IReadOnlyList<JournalEffect> Effects, IReadOnlyList<JournalReportedBalance> ReportedBalances);
