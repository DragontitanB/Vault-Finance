using System.Data;
using Microsoft.Data.SqlClient;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialInspectionSqlServerTests
{
    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticRole_IsReadOnlyEvenWithRuntimeMembershipAndOrdinaryRuntimeCannotInspect(bool diagnostic)
    {
        await Open(a, 100m); var before = await OwnerRows();
        string principal = "vf_inspection_probe_" + run; await using var admin = await Connect(); byte[] sid;
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE USER [{principal}] WITHOUT LOGIN; ALTER ROLE VaultFinanceFinancialRuntime ADD MEMBER [{principal}]; "
                + (diagnostic ? $"ALTER ROLE VaultFinanceJournalDiagnostics ADD MEMBER [{principal}]; " : "")
                + $"SELECT sid FROM sys.database_principals WHERE name=N'{principal}';";
            sid = (byte[])(await create.ExecuteScalarAsync())!;
        }
        try
        {
            await using var runtime = await Connect(); await using var command = runtime.CreateCommand();
            command.CommandText = $"EXECUTE AS USER=N'{principal}';"; await command.ExecuteNonQueryAsync();
            try
            {
                command.Parameters.AddWithValue("@owner", owner); command.Parameters.AddWithValue("@account", a.Id);
                if (!diagnostic)
                {
                    command.CommandText = "EXEC dbo.CaptureFinancialJournalInspection @owner;";
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                }
                else
                {
                    foreach (string sql in new[]
                    {
                        "SELECT COUNT(*) FROM dbo.Accounts;", "SELECT COUNT(*) FROM dbo.FinancialOperations;",
                        "INSERT INTO dbo.FinancialOperations (Id,UserId,BatchId,Kind,Currency,Amount,DestinationAccountId) SELECT NEWID(),@owner,NEWID(),2,'USD',1,@account WHERE 1=0;",
                        "UPDATE dbo.Accounts SET Status=Status WHERE 1=0;", "DELETE FROM dbo.FinancialAuditEntries WHERE 1=0;",
                        "EXEC dbo.InitializeFinancialOpening @owner,@account,N'0.00',N'USD',N'Synthetic',N'forbidden';",
                        "EXEC dbo.RecordFinancialIncome @owner,@account,N'1.00',N'USD',NULL,N'forbidden';",
                        "EXEC dbo.RecordFinancialExpense @owner,@account,N'1.00',N'USD',NULL,N'forbidden';",
                        "EXEC dbo.RecordFinancialTransfer @owner,@account,@account,N'1.00',N'USD',NULL,N'forbidden';",
                        "EXEC dbo.RecordFinancialReversal @owner,@account,N'Synthetic',N'forbidden';",
                        "EXEC dbo.CorrectFinancialOpening @owner,@account,N'80.00',N'USD',1,N'Synthetic',N'forbidden';",
                        "EXEC dbo.AcquireFinancialAccountLocks @owner,@account;"
                    })
                    {
                        command.CommandText = sql; Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                    }
                    command.CommandText = "EXEC dbo.CaptureFinancialJournalInspection @owner;";
                    await using var results = await command.ExecuteReaderAsync();
                    Assert.True(await results.ReadAsync()); Assert.Equal(owner, results.GetGuid(1));
                    bool complete = false; int sets = 1;
                    while (await results.NextResultAsync())
                    {
                        sets++;
                        if (results.FieldCount == 4 && results.GetName(2) == "IsComplete")
                        { Assert.True(await results.ReadAsync()); complete = results.GetBoolean(2); Assert.Equal(owner, results.GetGuid(1)); }
                        else while (await results.ReadAsync()) { }
                    }
                    Assert.True(complete); Assert.Equal(11, sets); // Header + seven data sets + two usual balances + completion.
                }
            }
            finally { command.Parameters.Clear(); command.CommandText = "REVERT;"; await command.ExecuteNonQueryAsync(); }
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"""
                IF DB_NAME()<>N'VaultFinanceJournalTests'
                    OR NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=N'{principal}' AND sid=@sid AND type='S')
                    THROW 51081,'Diagnostic test principal ownership changed; cleanup refused.',1;
                """ + (diagnostic ? $"ALTER ROLE VaultFinanceJournalDiagnostics DROP MEMBER [{principal}]; " : "")
                + $"ALTER ROLE VaultFinanceFinancialRuntime DROP MEMBER [{principal}]; DROP USER [{principal}];";
            drop.Parameters.Add("@sid", SqlDbType.VarBinary, 85).Value = sid; await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await OwnerRows());
    }

    [SqlServerFact]
    public async Task CancelledCapture_IsAnErrorAndLeavesNoFinancialWrites()
    {
        await Open(a, 100m); var before = await OwnerRows();
        await using var blocker = await Connect(); await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await Hold(blocker, transaction, "Accounts"); using var cancellation = new CancellationTokenSource();
        var capture = reader.CaptureAsync(owner, cancellation.Token);
        try
        { await WaitForBlocked(1, ".reader"); cancellation.Cancel(); Assert.Equal("inspection.cancelled", (await capture).ErrorCode); }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(before, await OwnerRows());
    }
}
