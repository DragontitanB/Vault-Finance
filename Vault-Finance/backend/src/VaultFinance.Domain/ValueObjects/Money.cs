using VaultFinance.Domain.Exceptions;

namespace VaultFinance.Domain.ValueObjects;

public sealed record Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("Money amount must have at most two decimal places; rounding is not implicit.");
        }

        Amount = amount;
        Currency = CurrencyCode.Normalize(currency);
    }

    public Money Add(Money other)
    {
        EnsureCompatibleCurrency(other);

        try
        {
            var amount = checked(Amount + other.Amount);

            // Decimal arithmetic can round at large magnitudes without throwing.
            if (amount - Amount != other.Amount || amount - other.Amount != Amount)
            {
                throw new DomainException("Money addition would lose decimal precision.");
            }

            return new Money(amount, Currency);
        }
        catch (OverflowException exception)
        {
            throw new DomainException("Money addition exceeds the decimal range.", exception);
        }
    }

    public Money Subtract(Money other)
    {
        EnsureCompatibleCurrency(other);

        try
        {
            var amount = checked(Amount - other.Amount);

            if (Amount - amount != other.Amount || amount + other.Amount != Amount)
            {
                throw new DomainException("Money subtraction would lose decimal precision.");
            }

            return new Money(amount, Currency);
        }
        catch (OverflowException exception)
        {
            throw new DomainException("Money subtraction exceeds the decimal range.", exception);
        }
    }

    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Add(right);
    }

    public static Money operator -(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Subtract(right);
    }

    private void EnsureCompatibleCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Currency != other.Currency)
        {
            throw new DomainException("Money operations require matching currencies; currency conversion is not supported.");
        }
    }
}
