using System.Globalization;
using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.UnitTests.FinancialOperations;

public sealed class FinancialAmountTests
{
    [Theory]
    [InlineData("0.01", "DOP")]
    [InlineData("1.2300", " usd ")]
    [InlineData("99999999999999999999999999.99", "USD")]
    public void Constructor_AcceptsPositiveExactMagnitudes(string input, string currency)
    {
        var money = new Money(decimal.Parse(input, CultureInfo.InvariantCulture), currency);
        var amount = new FinancialAmount(money);

        Assert.Same(money, amount.Value);
        Assert.Contains(amount.Value.Currency, new[] { "DOP", "USD" });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-100")]
    [InlineData("100000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public void Constructor_RejectsNonPositiveOrExcessiveAmounts(string input)
    {
        var money = new Money(decimal.Parse(input, CultureInfo.InvariantCulture), "USD");
        Assert.Throws<DomainException>(() => new FinancialAmount(money));
    }

    [Fact]
    public void Maximum_IsExactlySqlDecimal28Scale2UpperBound()
    {
        Assert.Equal(FinancialAmount.MaximumValue, decimal.Parse("99999999999999999999999999.99",
            CultureInfo.InvariantCulture));
        Assert.Throws<DomainException>(() =>
            new FinancialAmount(new Money(FinancialAmount.MaximumValue + 0.01m, "DOP")));
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    [InlineData("US")]
    public void InvalidCurrency_IsRejectedByMoney(string currency)
    {
        Assert.Throws<DomainException>(() => new FinancialAmount(new Money(1m, currency)));
    }

    [Fact]
    public void ExcessPrecision_IsRejectedWithoutRounding()
    {
        Assert.Throws<DomainException>(() => new FinancialAmount(new Money(1.001m, "USD")));
    }

    [Fact]
    public void Equality_UsesMoneyValueAndNormalizedCurrency()
    {
        var first = new FinancialAmount(new Money(1.23m, " usd "));
        var equal = new FinancialAmount(new Money(1.2300m, "USD"));

        Assert.Equal(first, equal);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(first, new FinancialAmount(new Money(1.23m, "DOP")));
        Assert.NotEqual(first, new FinancialAmount(new Money(2m, "USD")));
    }

    [Fact]
    public void Constructor_RejectsNullMoney()
    {
        Assert.Throws<ArgumentNullException>(() => new FinancialAmount(null!));
    }

    [Fact]
    public void FinancialBound_DoesNotChangeGenericMoneyContract()
    {
        Assert.Equal(decimal.MaxValue, new Money(decimal.MaxValue, "USD").Amount);
        Assert.Equal(decimal.MinValue, new Money(decimal.MinValue, "DOP").Amount);
        Assert.Equal(0m, new Money(0m, "USD").Amount);
    }
}
