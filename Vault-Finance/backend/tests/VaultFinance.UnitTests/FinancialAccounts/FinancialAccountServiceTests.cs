using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.UnitTests.FinancialAccounts;

public sealed class FinancialAccountServiceTests
{
    [Theory]
    [InlineData("0", " dop ")]
    [InlineData("1.2300", "usd")]
    [InlineData("99999999999999999999999999.99", "USD")]
    public async Task Opening_NormalizesValidIntentAndKeepsOwnerOutsideTheRequest(string amount, string currency)
    {
        var journal = new CapturingJournal();
        var service = new FinancialAccountService(journal);
        var owner = new FinancialOwner(Guid.NewGuid());
        var account = Guid.NewGuid();
        decimal value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        await service.InitializeOpeningAsync(owner, new(account, value, currency, "  Synthetic opening  "), "Key_1");

        Assert.Same(owner, journal.Owner);
        Assert.Equal(new ValidatedOpening(account, value, currency.Trim().ToUpperInvariant(), "Synthetic opening", "Key_1"), journal.Opening);
        Assert.Equal(1, journal.Calls);
        Assert.DoesNotContain(typeof(InitializeOpeningRequest).GetProperties(), property =>
            property.Name is "UserId" or "OwnerId" or "RecordedAtUtc" or "EffectiveDate");
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("100000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public async Task Opening_RejectsNegativeExcessPrecisionAndOutOfRangeAmountsBeforePersistence(string amount)
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "USD", "Synthetic"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    [InlineData("US")]
    [InlineData("ＵＳＤ")]
    public async Task Opening_RejectsInvalidCurrencyBeforePersistence(string? currency)
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), 1m, currency, "Synthetic"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Opening_RequiresReason(string? reason)
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), 1m, "USD", reason));
    }

    [Fact]
    public async Task Opening_RejectsLongReasonAndEmptyAccountIdentifier()
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), 1m, "USD", new string('a', FinancialOperation.MaximumReasonLength + 1)));
        await AssertInvalidAsync(new(Guid.Empty, 1m, "USD", "Synthetic"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("Ｋey")]
    [InlineData("key\0")]
    public async Task Opening_RejectsInvalidIdempotencyKey(string? key)
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), 1m, "USD", "Synthetic"), key!);
    }

    [Fact]
    public async Task Opening_RejectsLongIdempotencyKey()
    {
        await AssertInvalidAsync(new(Guid.NewGuid(), 1m, "USD", "Synthetic"), new string('a', 129));
    }

    [Fact]
    public void OwnerContext_RequiresANonEmptyIdentity()
    {
        Assert.Throws<ArgumentException>(() => new FinancialOwner(Guid.Empty));
    }

    [Fact]
    public async Task Balance_ValidatesIdentifierAndPassesTrustedOwnerToThePort()
    {
        var journal = new CapturingJournal();
        var service = new FinancialAccountService(journal);
        var owner = new FinancialOwner(Guid.NewGuid());
        Assert.Equal(FinancialFailure.InvalidInput, (await service.GetBalanceAsync(owner, Guid.Empty)).Failure);
        Assert.Equal(0, journal.Calls);
        var account = Guid.NewGuid();
        var result = await service.GetBalanceAsync(owner, account);
        Assert.Equal(FinancialFailure.NotFound, result.Failure);
        Assert.Same(owner, journal.Owner);
        Assert.Equal(account, journal.AccountId);
    }

    private static async Task AssertInvalidAsync(InitializeOpeningRequest request, string key = "key")
    {
        var journal = new CapturingJournal();
        var outcome = await new FinancialAccountService(journal).InitializeOpeningAsync(new(Guid.NewGuid()), request, key);
        Assert.Equal(FinancialFailure.InvalidInput, outcome.Failure);
        Assert.NotNull(outcome.Errors);
        Assert.NotEmpty(outcome.Errors);
        Assert.Equal(0, journal.Calls);
    }

    private sealed class CapturingJournal : IFinancialJournal
    {
        public int Calls { get; private set; }
        public FinancialOwner? Owner { get; private set; }
        public ValidatedOpening? Opening { get; private set; }
        public Guid AccountId { get; private set; }

        public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken)
        {
            Calls++; Owner = owner; Opening = opening;
            return Task.FromResult(new OpeningOutcome(Failure: FinancialFailure.NotFound));
        }

        public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken)
        {
            Calls++; Owner = owner; AccountId = accountId;
            return Task.FromResult(new BalanceOutcome(Failure: FinancialFailure.NotFound));
        }
    }
}
