using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Read-only view row: signed account effect, never a stored or calculated balance.</summary>
public sealed class FinancialAccountMovement
{
    public Guid OperationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AccountId { get; private set; }
    public string Currency { get; private set; } = null!;
    public Guid BatchId { get; private set; }
    public long BatchSequence { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public DateOnly EffectiveDate { get; private set; }
    public FinancialOperationKind Kind { get; private set; }
    public decimal SignedAmount { get; private set; }
}
