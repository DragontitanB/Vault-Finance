using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.UnitTests.FinancialOperations;

public sealed class FinancialPostingContextTests
{
    [Theory]
    [InlineData(3, 59, 59, 23)]
    [InlineData(4, 0, 0, 24)]
    public void EffectiveDate_UsesSantoDomingoCalendar(int hour, int minute, int second, int day)
    {
        var batchId = Guid.NewGuid();
        var instant = new DateTimeOffset(2026, 11, 24, hour, minute, second, TimeSpan.Zero);
        var context = new FinancialPostingContext(batchId, instant);

        Assert.Equal(batchId, context.BatchId);
        Assert.Equal(instant, context.RecordedAtUtc);
        Assert.Equal(new DateOnly(2026, 11, day), context.EffectiveDate);
        Assert.Equal("America/Santo_Domingo", FinancialPostingContext.AccountingTimeZoneId);
        Assert.Equal(context, new FinancialPostingContext(batchId, instant));
    }

    [Fact]
    public void Constructor_RejectsEmptyBatchId()
    {
        Assert.Throws<DomainException>(() => new FinancialPostingContext(Guid.Empty, Instant));
    }

    [Fact]
    public void Constructor_RejectsDefaultInstant()
    {
        Assert.Throws<DomainException>(() => new FinancialPostingContext(Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(1)]
    public void Constructor_RejectsNonZeroOffsetEvenForAnEquivalentInstant(int offsetHours)
    {
        var offset = Instant.ToOffset(TimeSpan.FromHours(offsetHours));
        Assert.Throws<DomainException>(() => new FinancialPostingContext(Guid.NewGuid(), offset));
    }

    private static DateTimeOffset Instant => new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
}
