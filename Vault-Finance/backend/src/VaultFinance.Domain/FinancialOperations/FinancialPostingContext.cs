using VaultFinance.Domain.Exceptions;

namespace VaultFinance.Domain.FinancialOperations;

/// <summary>Deterministic recording metadata shared by an indivisible posting batch.</summary>
public sealed record FinancialPostingContext
{
    public const string AccountingTimeZoneId = "America/Santo_Domingo";

    private static readonly TimeZoneInfo AccountingTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(AccountingTimeZoneId);

    public Guid BatchId { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public DateOnly EffectiveDate { get; }

    public FinancialPostingContext(Guid batchId, DateTimeOffset recordedAtUtc)
    {
        if (batchId == Guid.Empty)
        {
            throw new DomainException("A posting batch identifier is required.");
        }

        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new DomainException("A non-default UTC recording instant with zero offset is required.");
        }

        BatchId = batchId;
        RecordedAtUtc = recordedAtUtc;
        EffectiveDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(recordedAtUtc, AccountingTimeZone).DateTime);
    }
}
