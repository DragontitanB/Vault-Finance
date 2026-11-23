namespace VaultFinance.Domain.FinancialOperations;

/// <summary>Immutable journal representation; does not establish SQL durability.</summary>
public enum FinancialOperationStatus
{
    Confirmed = 1
}
