using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.UnitTests.FinancialAccounts;

public sealed class FinancialEntryServiceTests
{
    [Theory]
    [InlineData(true, "0.01", " dop ", null)]
    [InlineData(false, "1.2300", "usd", "  Synthetic entry  ")]
    [InlineData(true, "99999999999999999999999999.99", "USD", "")]
    [InlineData(false, "99999999999999999999999999.99", "DOP", " \t ")]
    public async Task Entry_UsesPositiveMoneyAndNormalizesTheFullIntent(bool income, string text, string currency, string? reason)
    {
        var journal = new CapturingJournal();
        var owner = new FinancialOwner(Guid.NewGuid());
        var request = new FinancialEntryRequest(Guid.NewGuid(), decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), currency, reason);
        var result = await Invoke(new(journal), owner, request, "Key_1", income);
        Assert.Equal(journal.Result, result);
        Assert.Same(owner, journal.Owner);
        Assert.Equal(income, journal.Income);
        Assert.Equal(new ValidatedFinancialEntry(request.AccountId, request.Amount, currency.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), "Key_1"), journal.Entry);
        Assert.Equal(1, journal.Calls);
    }

    [Theory]
    [InlineData(true, "0")]
    [InlineData(false, "0")]
    [InlineData(true, "-1")]
    [InlineData(false, "-0.01")]
    [InlineData(true, "1.001")]
    [InlineData(false, "1.001")]
    [InlineData(true, "100000000000000000000000000")]
    [InlineData(false, "79228162514264337593543950335")]
    public async Task Entry_RejectsNonPositiveExcessPrecisionAndOutOfRangeAmounts(bool income, string amount)
    {
        await Invalid(new(Guid.NewGuid(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "USD"), "key", income);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "EUR")]
    [InlineData(false, "US")]
    [InlineData(true, "ＵＳＤ")]
    public async Task Entry_RejectsInvalidCurrency(bool income, string? currency)
    {
        await Invalid(new(Guid.NewGuid(), 1m, currency), "key", income);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entry_RejectsEmptyAccountAndLongReason(bool income)
    {
        await Invalid(new(Guid.Empty, 1m, "USD"), "key", income);
        await Invalid(new(Guid.NewGuid(), 1m, "USD", new string('a', FinancialOperation.MaximumReasonLength + 1)), "key", income);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "bad key")]
    [InlineData(false, "Ｋey")]
    [InlineData(true, "key\0")]
    public async Task Entry_RejectsInvalidKey(bool income, string? key)
    {
        await Invalid(new(Guid.NewGuid(), 1m, "USD"), key!, income);
    }

    [Fact]
    public async Task Entry_RejectsLongKeyAndKeepsActorKindAndDatesOutsideTheRequest()
    {
        await Invalid(new(Guid.NewGuid(), 1m, "USD"), new string('a', 129), true);
        Assert.DoesNotContain(typeof(FinancialEntryRequest).GetProperties(),
            property => property.Name is "UserId" or "OwnerId" or "Kind" or "RecordedAtUtc" or "EffectiveDate");
    }

    private static async Task Invalid(FinancialEntryRequest request, string key, bool income)
    {
        var journal = new CapturingJournal();
        var result = await Invoke(new(journal), new(Guid.NewGuid()), request, key, income);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.NotNull(result.Errors);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, journal.Calls);
    }

    private static Task<FinancialEntryOutcome> Invoke(FinancialAccountService service, FinancialOwner owner,
        FinancialEntryRequest request, string key, bool income) =>
        income ? service.RecordIncomeAsync(owner, request, key) : service.RecordExpenseAsync(owner, request, key);

    private sealed class CapturingJournal : IFinancialJournal
    {
        public Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, ValidatedFinancialTransfer transfer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public int Calls { get; private set; }
        public FinancialOwner? Owner { get; private set; }
        public ValidatedFinancialEntry? Entry { get; private set; }
        public bool Income { get; private set; }
        public FinancialEntryOutcome Result { get; } = new(Failure: FinancialFailure.Uninitialized);
        public Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) =>
            Capture(owner, entry, true);
        public Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) =>
            Capture(owner, entry, false);
        private Task<FinancialEntryOutcome> Capture(FinancialOwner owner, ValidatedFinancialEntry entry, bool income)
        {
            Calls++; Owner=owner; Entry=entry; Income=income;
            return Task.FromResult(Result);
        }
        public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
