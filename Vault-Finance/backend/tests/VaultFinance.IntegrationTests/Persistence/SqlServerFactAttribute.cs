namespace VaultFinance.IntegrationTests.Persistence;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1")
        {
            Skip = "Set VAULTFINANCE_SQL_TESTS=1 and configure SqlTestConnection for the marked VaultFinanceJournalTests database.";
        }
    }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1")
        {
            Skip = "Set VAULTFINANCE_SQL_TESTS=1 and configure SqlTestConnection for the marked VaultFinanceJournalTests database.";
        }
    }
}
