using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.Domain.FinancialOperations;

/// <summary>A positive monetary magnitude supported by the financial journal.</summary>
public sealed record FinancialAmount
{
    public const decimal MaximumValue = 99_999_999_999_999_999_999_999_999.99m;

    public Money Value { get; }

    public FinancialAmount(Money value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Amount <= 0m || value.Amount > MaximumValue)
        {
            throw new DomainException("Financial amounts must be positive and within the supported journal limit.");
        }

        Value = value;
    }
}
