using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Application.FinancialAccounts;

/// <summary>Trusted server context, never a field of a client request.</summary>
public sealed record FinancialOwner
{
    public Guid UserId { get; }

    public FinancialOwner(Guid userId)
    {
        if (userId == Guid.Empty) { throw new ArgumentException("An authenticated owner identifier is required.", nameof(userId)); }
        UserId = userId;
    }
}

public sealed record InitializeOpeningRequest(Guid AccountId, decimal Amount, string? Currency, string? Reason);

public sealed record ValidatedOpening(Guid AccountId, decimal Amount, string Currency, string Reason, string IdempotencyKey);

public sealed record CorrectOpeningRequest(Guid AccountId, decimal Amount, string? Currency, int ExpectedRevision, string? Reason);

public sealed record ValidatedOpeningCorrection(Guid AccountId, decimal Amount, string Currency, int ExpectedRevision, string Reason, string IdempotencyKey);

public sealed record OpeningCorrectionReceipt(
    int ReceiptVersion, Guid AccountId, Guid BatchId, int PreviousRevision, int Revision,
    Guid? PreviousOpeningOperationId, Guid? CompensationOperationId, Guid? OpeningOperationId,
    decimal PreviousOpeningAmount, decimal OpeningAmount, string Currency, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record OpeningCorrectionOutcome(
    OpeningCorrectionReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record FinancialEntryRequest(Guid AccountId, decimal Amount, string? Currency, string? Reason = null);

public sealed record ValidatedFinancialEntry(Guid AccountId, decimal Amount, string Currency, string? Reason, string IdempotencyKey);

public sealed record FinancialTransferRequest(Guid SourceAccountId, Guid DestinationAccountId,
    decimal Amount, string? Currency, string? Reason = null);

public sealed record ValidatedFinancialTransfer(Guid SourceAccountId, Guid DestinationAccountId,
    decimal Amount, string Currency, string? Reason, string IdempotencyKey);

public sealed record FinancialReversalRequest(Guid OriginalOperationId, string? Reason);

public sealed record ValidatedFinancialReversal(Guid OriginalOperationId, string Reason, string IdempotencyKey);

public enum FinancialFailure
{
    InvalidInput,
    NotFound,
    Archived,
    CurrencyMismatch,
    AlreadyInitialized,
    IdempotencyConflict,
    JournalInconsistent,
    TemporarilyUnavailable,
    CommitOutcomeUnknown,
    Uninitialized,
    InsufficientFunds,
    BalanceLimitExceeded,
    OperationNotReversible,
    AlreadyReversed,
    OpeningVersionConflict,
    OpeningUnchanged,
    OpeningRevisionLimitExceeded
}

public sealed record OpeningReceipt(
    int ReceiptVersion, Guid AccountId, Guid BatchId, Guid? OperationId, int Revision,
    decimal OpeningAmount, string Currency, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record OpeningOutcome(
    OpeningReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record DerivedAccountBalance(Guid AccountId, string Currency, bool IsInitialized, decimal? Amount);

public sealed record BalanceOutcome(DerivedAccountBalance? Balance = null, FinancialFailure? Failure = null);

public sealed record FinancialEntryReceipt(
    int ReceiptVersion, Guid AccountId, Guid BatchId, Guid OperationId, FinancialOperationKind Kind,
    decimal Amount, string Currency, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record FinancialEntryOutcome(
    FinancialEntryReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record FinancialTransferReceipt(
    int ReceiptVersion, Guid SourceAccountId, Guid DestinationAccountId, Guid BatchId, Guid OperationId,
    FinancialOperationKind Kind, decimal Amount, string Currency, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record FinancialTransferOutcome(
    FinancialTransferReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public interface IFinancialAccountService
{
    Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, CorrectOpeningRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, FinancialReversalRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, FinancialTransferRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, FinancialEntryRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, FinancialEntryRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, InitializeOpeningRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>The narrow SQL protocol boundary, not a repository or a set of independent inserts.</summary>
public interface IFinancialJournal
{
    Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, ValidatedOpeningCorrection correction,
        CancellationToken cancellationToken);
    Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, ValidatedFinancialReversal reversal,
        CancellationToken cancellationToken);
    Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, ValidatedFinancialTransfer transfer,
        CancellationToken cancellationToken);
    Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, ValidatedFinancialEntry entry,
        CancellationToken cancellationToken);
    Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, ValidatedFinancialEntry entry,
        CancellationToken cancellationToken);
    Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening,
        CancellationToken cancellationToken);
    Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken);
}

public sealed record FinancialReversalReceipt(
    int ReceiptVersion, Guid OriginalOperationId, FinancialOperationKind OriginalOperationKind,
    Guid BatchId, Guid OperationId, FinancialOperationKind Kind, decimal Amount, string Currency,
    Guid? DebitAccountId, Guid? CreditAccountId, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record FinancialReversalOutcome(
    FinancialReversalReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
