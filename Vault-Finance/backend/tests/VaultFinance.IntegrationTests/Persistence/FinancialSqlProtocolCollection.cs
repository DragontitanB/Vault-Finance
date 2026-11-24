namespace VaultFinance.IntegrationTests.Persistence;

// Test-owned triggers share SQL table metadata. Serialize fixture classes, while the
// concurrency tests still create independent contending SQL sessions within each case.
[CollectionDefinition(FinancialSqlProtocolCollection.Name, DisableParallelization = true)]
public sealed class FinancialSqlProtocolCollection
{
    public const string Name = "Financial SQL protocol";
}
