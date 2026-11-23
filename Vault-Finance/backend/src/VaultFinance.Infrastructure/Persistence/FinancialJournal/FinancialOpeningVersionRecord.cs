using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Immutable opening metadata; a null operation explicitly records zero.</summary>
public sealed class FinancialOpeningVersionRecord
{
    public Guid UserId { get; private set; }
    public string Currency { get; private set; }
    public Guid AccountId { get; private set; }
    public int Revision { get; private set; }
    public int? PreviousRevision { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid? OpeningOperationId { get; private set; }
    public FinancialOperationKind? OpeningOperationKind { get; private set; }
    public string Reason { get; private set; }

    public FinancialOpeningVersionRecord(Guid userId, string currency, Guid accountId, int revision,
        int? previousRevision, Guid batchId, Guid? openingOperationId, string reason)
    {
        UserId = userId; Currency = currency; AccountId = accountId; Revision = revision;
        PreviousRevision = previousRevision; BatchId = batchId; OpeningOperationId = openingOperationId;
        OpeningOperationKind = openingOperationId.HasValue ? FinancialOperationKind.Opening : null;
        Reason = reason;
    }
}
