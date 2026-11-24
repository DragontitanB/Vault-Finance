using System.Globalization;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.UnitTests.FinancialAccounts;

public sealed class FinancialTransferServiceTests
{
    [Theory]
    [InlineData(" dop ", "0.01", null)]
    [InlineData("usd", "1.2300", "  Synthetic transfer  ")]
    [InlineData("USD", "99999999999999999999999999.99", " \t ")]
    public async Task Transfer_NormalizesIntentAndPassesTheSeparateTrustedOwner(string currency, string amount, string? reason)
    {
        var journal = new CapturingJournal();
        var owner = new FinancialOwner(Guid.NewGuid());
        var request = new FinancialTransferRequest(Guid.NewGuid(), Guid.NewGuid(), Number(amount), currency, reason);
        using var cancellation = new CancellationTokenSource();
        var result = await new FinancialAccountService(journal).RecordTransferAsync(owner, request, "Key_1", cancellation.Token);
        Assert.Same(journal.Result, result);
        Assert.Same(owner, journal.Owner);
        Assert.Equal(new ValidatedFinancialTransfer(request.SourceAccountId, request.DestinationAccountId, request.Amount,
            currency.Trim().ToUpperInvariant(), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), "Key_1"), journal.Transfer);
        Assert.Equal(cancellation.Token, journal.Token);
        Assert.Equal(1, journal.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("100000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public async Task Transfer_RejectsNonPositiveExcessPrecisionOrExcessRange(string amount) =>
        await Invalid(new(Guid.NewGuid(), Guid.NewGuid(), Number(amount), "USD"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    [InlineData("US")]
    public async Task Transfer_RejectsUnsupportedCurrencies(string? currency) =>
        await Invalid(new(Guid.NewGuid(), Guid.NewGuid(), 1m, currency));

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("same")]
    public async Task Transfer_RequiresTwoDistinctNonemptyIdentifiers(string invalid)
    {
        var source = invalid == "source" ? Guid.Empty : Guid.NewGuid();
        var destination = invalid == "destination" ? Guid.Empty : invalid == "same" ? source : Guid.NewGuid();
        await Invalid(new(source, destination, 1m, "USD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad key")]
    [InlineData("key\0")]
    public async Task Transfer_RejectsInvalidKeys(string? key) =>
        await Invalid(new(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD"), key!);

    [Fact]
    public async Task Transfer_RejectsLongKeysAndReasons()
    {
        await Invalid(new(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD"), new string('a', 129));
        await Invalid(new(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD", new string('a', 201)));
    }

    [Fact]
    public async Task Transfer_RequiresAnExplicitOwnerAndKeepsPrivilegedFieldsOutsideTheRequest()
    {
        var service = new FinancialAccountService(new CapturingJournal());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecordTransferAsync(null!, new(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD"), "key"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecordTransferAsync(new(Guid.NewGuid()), null!, "key"));
        Assert.DoesNotContain(typeof(FinancialTransferRequest).GetProperties(),
            property => property.Name is "UserId" or "OwnerId" or "Kind" or "RecordedAtUtc" or "EffectiveDate");
        Assert.DoesNotContain(typeof(FinancialTransferReceipt).GetProperties(), property => property.Name.Contains("Balance", StringComparison.Ordinal));
    }

    private static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
    private static async Task Invalid(FinancialTransferRequest request, string key = "key")
    {
        var journal = new CapturingJournal();
        var result = await new FinancialAccountService(journal).RecordTransferAsync(new(Guid.NewGuid()), request, key);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.NotEmpty(result.Errors!);
        Assert.Equal(0, journal.Calls);
    }

    private sealed class CapturingJournal : IFinancialJournal
    {
        public Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, ValidatedOpeningCorrection correction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, ValidatedFinancialReversal reversal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public int Calls { get; private set; }
        public FinancialOwner? Owner { get; private set; }
        public ValidatedFinancialTransfer? Transfer { get; private set; }
        public CancellationToken Token { get; private set; }
        public FinancialTransferOutcome Result { get; } = new(Failure: FinancialFailure.Uninitialized);
        public Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, ValidatedFinancialTransfer transfer, CancellationToken cancellationToken)
        {
            Calls++; Owner = owner; Transfer = transfer; Token = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
