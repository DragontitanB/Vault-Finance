namespace VaultFinance.IntegrationTests.Persistence;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1")
        {
            Skip = "Set VAULTFINANCE_SQL_TESTS=1 to run against the migrated local VaultFinanceDb.";
        }
    }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1")
        {
            Skip = "Set VAULTFINANCE_SQL_TESTS=1 to run against the migrated local VaultFinanceDb.";
        }
    }
}
