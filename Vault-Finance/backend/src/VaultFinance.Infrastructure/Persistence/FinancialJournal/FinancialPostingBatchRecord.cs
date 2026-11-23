using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Stored facts; materialization preserves the recorded accounting date.</summary>
public sealed class FinancialPostingBatchRecord
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public long Sequence { get; private set; }
    public FinancialCommandType CommandType { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public DateOnly EffectiveDate { get; private set; }
    public string AccountingTimeZone { get; private set; } = null!;

    private FinancialPostingBatchRecord() { }

    public static FinancialPostingBatchRecord FromDomain(
        Guid userId, FinancialCommandType commandType, FinancialPostingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FinancialPostingBatchRecord
        {
            Id = context.BatchId, UserId = userId, CommandType = commandType,
            RecordedAtUtc = context.RecordedAtUtc, EffectiveDate = context.EffectiveDate,
            AccountingTimeZone = FinancialPostingContext.AccountingTimeZoneId
        };
    }
}
