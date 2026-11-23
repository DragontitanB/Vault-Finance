namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

public sealed class FinancialAuditEntryRecord
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OutcomeId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public string Action { get; private set; }
    public string SummaryJson { get; private set; }

    public FinancialAuditEntryRecord(Guid id, Guid userId, Guid outcomeId,
        DateTimeOffset recordedAtUtc, string action, string summaryJson)
    {
        Id = id; UserId = userId; OutcomeId = outcomeId;
        RecordedAtUtc = recordedAtUtc; Action = action; SummaryJson = summaryJson;
    }
}
