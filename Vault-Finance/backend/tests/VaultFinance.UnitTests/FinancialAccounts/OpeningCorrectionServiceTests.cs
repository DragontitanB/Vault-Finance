using System.Globalization;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.UnitTests.FinancialAccounts;

public sealed class OpeningCorrectionServiceTests
{
    [Theory]
    [InlineData("0", " dop ", 1)]
    [InlineData("1.2300", "usd", 5)]
    [InlineData("99999999999999999999999999.99", "USD", int.MaxValue)]
    public async Task Correction_NormalizesTheNewIntentAndPassesTheSeparateOwner(string amount, string currency, int revision)
    {
        var journal = new CapturingJournal(); var owner = new FinancialOwner(Guid.NewGuid());
        var request = new CorrectOpeningRequest(Guid.NewGuid(), Number(amount), currency, revision, "  Synthetic correction  ");
        using var cancellation = new CancellationTokenSource();
        var result = await new FinancialAccountService(journal).CorrectOpeningAsync(owner, request, "Key_1", cancellation.Token);
        Assert.Same(journal.Result, result); Assert.Same(owner, journal.Owner); Assert.Equal(cancellation.Token, journal.Token);
        Assert.Equal(new(request.AccountId, request.Amount, currency.Trim().ToUpperInvariant(), revision, "Synthetic correction", "Key_1"), journal.Correction);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("100000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public async Task Correction_RejectsNegativeExcessPrecisionOrRange(string amount) => await Invalid(Request() with { Amount = Number(amount) });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    public async Task Correction_RejectsUnsupportedCurrency(string? currency) => await Invalid(Request() with { Currency = currency });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Correction_RequiresAPositiveExpectedRevision(int revision) => await Invalid(Request() with { ExpectedRevision = revision });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Correction_RequiresAnAuditableReason(string? reason) => await Invalid(Request() with { Reason = reason });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad key")]
    [InlineData("key\0")]
    public async Task Correction_RequiresAValidKey(string? key) => await Invalid(Request(), key!);

    [Fact]
    public async Task Correction_RejectsEmptyIdAndExcessiveText()
    {
        await Invalid(Request() with { AccountId = Guid.Empty }); await Invalid(Request() with { Reason = new string('r', 201) });
        await Invalid(Request(), new string('k', 129));
    }

    [Fact]
    public async Task Correction_RequiresOwnerAndExposesNoHistoricalEffectInputs()
    {
        var service = new FinancialAccountService(new CapturingJournal());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CorrectOpeningAsync(null!, Request(), "key"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CorrectOpeningAsync(new(Guid.NewGuid()), null!, "key"));
        Assert.Equal(new[] { "AccountId", "Amount", "Currency", "ExpectedRevision", "Reason" }, typeof(CorrectOpeningRequest).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain(typeof(OpeningCorrectionReceipt).GetProperties(), p => p.Name.Contains("Balance", StringComparison.Ordinal));
    }

    private static CorrectOpeningRequest Request() => new(Guid.NewGuid(), 1m, "USD", 1, "Synthetic correction");
    private static decimal Number(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static async Task Invalid(CorrectOpeningRequest request, string key = "key")
    {
        var journal = new CapturingJournal(); var result = await new FinancialAccountService(journal).CorrectOpeningAsync(new(Guid.NewGuid()), request, key);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure); Assert.NotEmpty(result.Errors!); Assert.Null(journal.Correction);
    }
    private sealed class CapturingJournal : IFinancialJournal
    {
        public FinancialOwner? Owner { get; private set; }
        public ValidatedOpeningCorrection? Correction { get; private set; }
        public CancellationToken Token { get; private set; }
        public OpeningCorrectionOutcome Result { get; } = new(Failure: FinancialFailure.OpeningVersionConflict);
        public Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, ValidatedOpeningCorrection correction, CancellationToken cancellationToken)
        { Owner = owner; Correction = correction; Token = cancellationToken; return Task.FromResult(Result); }
        public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, ValidatedFinancialTransfer transfer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, ValidatedFinancialReversal reversal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
