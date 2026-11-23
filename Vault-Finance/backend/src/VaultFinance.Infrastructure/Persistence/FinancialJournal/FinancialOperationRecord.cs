using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Relational journal representation; not an executable financial operation.</summary>
public sealed class FinancialOperationRecord
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid BatchId { get; private set; }
    public FinancialOperationKind Kind { get; private set; }
    public string Currency { get; private set; } = null!;
    public decimal? Amount { get; private set; }
    public Guid? SourceAccountId { get; private set; }
    public Guid? DestinationAccountId { get; private set; }
    public Guid? OriginalOperationId { get; private set; }
    public FinancialOperationKind? OriginalOperationKind { get; private set; }
    public string? Reason { get; private set; }

    private FinancialOperationRecord() { }

    public static FinancialOperationRecord FromDomain(FinancialOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new FinancialOperationRecord
        {
            Id = operation.Id, UserId = operation.UserId, BatchId = operation.Posting.BatchId,
            Kind = operation.Kind, Currency = operation.Currency, Amount = operation.Amount?.Value.Amount,
            SourceAccountId = operation.SourceAccountId, DestinationAccountId = operation.DestinationAccountId,
            OriginalOperationId = operation.OriginalOperationId, OriginalOperationKind = operation.OriginalOperationKind,
            Reason = operation.Reason
        };
    }

    public FinancialAmount? ReadAmount() =>
        Amount is decimal amount ? new FinancialAmount(new Money(amount, Currency)) : null;
}
