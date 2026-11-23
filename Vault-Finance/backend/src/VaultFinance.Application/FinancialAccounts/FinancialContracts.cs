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
    CommitOutcomeUnknown
}

public sealed record OpeningReceipt(
    int ReceiptVersion, Guid AccountId, Guid BatchId, Guid? OperationId, int Revision,
    decimal OpeningAmount, string Currency, DateTimeOffset RecordedAtUtc, DateOnly EffectiveDate);

public sealed record OpeningOutcome(
    OpeningReceipt? Receipt = null, FinancialFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record DerivedAccountBalance(Guid AccountId, string Currency, bool IsInitialized, decimal? Amount);

public sealed record BalanceOutcome(DerivedAccountBalance? Balance = null, FinancialFailure? Failure = null);

public interface IFinancialAccountService
{
    Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, InitializeOpeningRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
    Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken = default);
}

/// <summary>The narrow SQL protocol boundary, not a repository or a set of independent inserts.</summary>
public interface IFinancialJournal
{
    Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening,
        CancellationToken cancellationToken);
    Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken);
}
