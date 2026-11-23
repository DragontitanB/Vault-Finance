using System.Globalization;
using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.UnitTests.ValueObjects;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("DOP", "DOP")]
    [InlineData("USD", "USD")]
    [InlineData("dop", "DOP")]
    [InlineData("uSd", "USD")]
    [InlineData(" dop ", "DOP")]
    [InlineData("\tusd\r\n", "USD")]
    public void Constructor_NormalizesSupportedCurrency(string currency, string expected)
    {
        var money = new Money(123.45m, currency);

        Assert.Equal(123.45m, money.Amount);
        Assert.Equal(expected, money.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("EUR")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("123")]
    [InlineData("U$D")]
    [InlineData("US D")]
    [InlineData("uſd")]
    [InlineData("ＵＳＤ")]
    public void Constructor_RejectsInvalidOrUnsupportedCurrency(string? currency)
    {
        var exception = Assert.Throws<DomainException>(() => new Money(10m, currency!));

        Assert.Contains("DOP or USD", exception.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-12.34")]
    [InlineData("0.01")]
    [InlineData("1.2300")]
    [InlineData("-1.2300")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void Constructor_AcceptsSignedAmountsAndInsignificantTrailingZeros(string value)
    {
        var amount = decimal.Parse(value, CultureInfo.InvariantCulture);

        var money = new Money(amount, "DOP");

        Assert.Equal(amount, money.Amount);
    }

    [Theory]
    [InlineData("1.001")]
    [InlineData("-1.001")]
    [InlineData("0.0001")]
    [InlineData("0.0000000000000000000000000001")]
    public void Constructor_RejectsExcessPrecisionInsteadOfRounding(string value)
    {
        var amount = decimal.Parse(value, CultureInfo.InvariantCulture);

        var exception = Assert.Throws<DomainException>(() => new Money(amount, "USD"));

        Assert.Contains("two decimal places", exception.Message);
    }

    [Fact]
    public void Equality_UsesAmountAndNormalizedCurrency()
    {
        var first = new Money(10.50m, " usd ");
        var second = new Money(10.5000m, "USD");

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new HashSet<Money> { first, second });
    }

    [Fact]
    public void Equality_DistinguishesDifferentAmountsAndCurrencies()
    {
        var money = new Money(10m, "USD");

        Assert.NotEqual(money, new Money(11m, "USD"));
        Assert.NotEqual(money, new Money(10m, "DOP"));
        Assert.True(money != new Money(10m, "DOP"));
    }

    [Theory]
    [InlineData("DOP")]
    [InlineData("USD")]
    public void Add_ReturnsExactSumWithoutChangingOperands(string currency)
    {
        var left = new Money(0.10m, currency);
        var right = new Money(0.20m, currency);

        var result = left.Add(right);

        Assert.Equal(new Money(0.30m, currency), result);
        Assert.Equal(result, left + right);
        Assert.NotSame(left, result);
        Assert.NotSame(right, result);
        Assert.Equal(0.10m, left.Amount);
        Assert.Equal(0.20m, right.Amount);
    }

    [Theory]
    [InlineData("DOP")]
    [InlineData("USD")]
    public void Subtract_CanReturnNegativeAmountWithoutChangingOperands(string currency)
    {
        var left = new Money(10.25m, currency);
        var right = new Money(15.50m, currency);

        var result = left.Subtract(right);

        Assert.Equal(new Money(-5.25m, currency), result);
        Assert.Equal(result, left - right);
        Assert.Equal(10.25m, left.Amount);
        Assert.Equal(15.50m, right.Amount);
    }

    [Fact]
    public void Arithmetic_SupportsNegativeOperandsAndZero()
    {
        var negative = new Money(-5.25m, "USD");
        var positive = new Money(5.25m, "USD");
        var zero = new Money(0m, "USD");

        Assert.Equal(zero, negative + positive);
        Assert.Equal(negative, negative + zero);
        Assert.Equal(zero, negative - negative);
        Assert.Equal(new Money(10.50m, "USD"), positive - negative);
    }

    [Theory]
    [InlineData("DOP", "USD")]
    [InlineData("USD", "DOP")]
    public void Arithmetic_RejectsIncompatibleCurrencies(string first, string second)
    {
        var left = new Money(10m, first);
        var right = new Money(2m, second);

        var addition = Assert.Throws<DomainException>(() => left.Add(right));
        var subtraction = Assert.Throws<DomainException>(() => left.Subtract(right));
        Assert.Throws<DomainException>(() => left + right);
        Assert.Throws<DomainException>(() => left - right);
        Assert.Contains("matching currencies", addition.Message);
        Assert.Contains("matching currencies", subtraction.Message);
    }

    [Fact]
    public void Arithmetic_RejectsNullOperands()
    {
        var money = new Money(1m, "USD");

        Assert.Throws<ArgumentNullException>(() => money.Add(null!));
        Assert.Throws<ArgumentNullException>(() => money.Subtract(null!));
        Assert.Throws<ArgumentNullException>(() => money + null!);
        Assert.Throws<ArgumentNullException>(() => money - null!);
        Assert.Throws<ArgumentNullException>(() => (null as Money)! + money);
        Assert.Throws<ArgumentNullException>(() => (null as Money)! - money);
    }

    [Fact]
    public void Add_ReportsDecimalOverflowAsDomainError()
    {
        var maximum = new Money(decimal.MaxValue, "USD");

        var exception = Assert.Throws<DomainException>(() => maximum.Add(new Money(1m, "USD")));

        Assert.Contains("decimal range", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void Subtract_ReportsDecimalOverflowAsDomainError()
    {
        var minimum = new Money(decimal.MinValue, "USD");

        var exception = Assert.Throws<DomainException>(() => minimum.Subtract(new Money(1m, "USD")));

        Assert.Contains("decimal range", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "0.01")]
    [InlineData("-79228162514264337593543950335", "-0.01")]
    public void Add_RejectsSilentPrecisionLoss(string first, string second)
    {
        var left = new Money(decimal.Parse(first, CultureInfo.InvariantCulture), "USD");
        var right = new Money(decimal.Parse(second, CultureInfo.InvariantCulture), "USD");

        var exception = Assert.Throws<DomainException>(() => left.Add(right));

        Assert.Contains("lose decimal precision", exception.Message);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "0.01")]
    [InlineData("-79228162514264337593543950335", "-0.01")]
    public void Subtract_RejectsSilentPrecisionLoss(string first, string second)
    {
        var left = new Money(decimal.Parse(first, CultureInfo.InvariantCulture), "USD");
        var right = new Money(decimal.Parse(second, CultureInfo.InvariantCulture), "USD");

        var exception = Assert.Throws<DomainException>(() => left.Subtract(right));

        Assert.Contains("lose decimal precision", exception.Message);
    }

    [Fact]
    public void Arithmetic_AllowsExactlyRepresentableDecimalBoundaries()
    {
        var maximum = new Money(decimal.MaxValue, "USD");
        var one = new Money(1m, "USD");
        var zero = new Money(0m, "USD");

        Assert.Equal(maximum, new Money(decimal.MaxValue - 1m, "USD") + one);
        Assert.Equal(maximum, maximum + zero);
        Assert.Equal(zero, maximum - maximum);
        Assert.Equal(new Money(decimal.MaxValue - 1m, "USD"), maximum - one);
    }
}
