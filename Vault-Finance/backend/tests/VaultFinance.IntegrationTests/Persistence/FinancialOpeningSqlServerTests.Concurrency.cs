using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialOpeningSqlServerTests
{
    [SqlServerTheory]
    [InlineData("identical-retry")]
    [InlineData("different-payload")]
    [InlineData("different-keys")]
    public async Task ConcurrentOpenings_UseDistinctBlockedSqlSessionsAndNeverDuplicateEffects(string scenario)
    {
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "EXEC dbo.AcquireFinancialAccountLocks @owner, @account;";
            command.Parameters.AddWithValue("@owner", ownerId);
            command.Parameters.AddWithValue("@account", account.Id);
            await command.ExecuteNonQueryAsync();
        }

        Task<OpeningOutcome> first = OpenAsync(1m, "race");
        Task<OpeningOutcome> second = OpenAsync(scenario == "different-payload" ? 2m : 1m,
            scenario == "different-keys" ? "another-race" : "race");
        var both = Task.WhenAll(first, second);
        try
        {
            int[] sessions = await WaitForBlockedSessionsAsync(2);
            Assert.Equal(2, sessions.Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            await both;
        }

        var results = await both;
        var success = results.First(result => result.Receipt is not null);
        if (scenario == "identical-retry")
        {
            Assert.Equal(results[0], results[1]);
            Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
        }
        else
        {
            Assert.Single(results, result => result.Receipt is not null);
            Assert.Equal(scenario == "different-keys" ? FinancialFailure.AlreadyInitialized : FinancialFailure.IdempotencyConflict,
                Assert.Single(results, result => result.Receipt is null).Failure);
            int outcomes = scenario == "different-keys" ? 2 : 1;
            Assert.Equal((1, 1, 1, outcomes, outcomes, 1), await CountsAsync());
        }
        Assert.Equal(success.Receipt!.OpeningAmount, (await service.GetBalanceAsync(Owner(), account.Id)).Balance!.Amount);
    }

    [SqlServerFact]
    public async Task DeterministicLocks_OrderTwoAccountsIdenticallyForReversedInputs()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync(applicationName);
        await using var firstTransaction = (SqlTransaction)await first.BeginTransactionAsync();
        await using var secondTransaction = (SqlTransaction)await second.BeginTransactionAsync();
        await using var command1 = first.CreateCommand();
        command1.Transaction = firstTransaction;
        command1.CommandText = "EXEC dbo.AcquireFinancialAccountLocks @owner, @first, @second;";
        command1.Parameters.AddWithValue("@owner", ownerId);
        command1.Parameters.AddWithValue("@first", account.Id);
        command1.Parameters.AddWithValue("@second", secondAccount.Id);
        await command1.ExecuteNonQueryAsync();
        await using var command2 = second.CreateCommand();
        command2.Transaction = secondTransaction;
        command2.CommandTimeout = 10;
        command2.CommandText = command1.CommandText;
        command2.Parameters.AddWithValue("@owner", ownerId);
        command2.Parameters.AddWithValue("@first", secondAccount.Id);
        command2.Parameters.AddWithValue("@second", account.Id);
        Task waiting = command2.ExecuteNonQueryAsync();
        try
        {
            await WaitForBlockedSessionsAsync(1);
            Assert.False(waiting.IsCompleted);
        }
        finally { await firstTransaction.RollbackAsync(); }
        await waiting;
        await secondTransaction.RollbackAsync();
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerFact]
    public async Task SqlLockTimeout_RetriesTheWholeSameKeyUnitAfterRollback()
    {
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using var command = blocker.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXEC dbo.AcquireFinancialAccountLocks @owner, @account;";
        command.Parameters.AddWithValue("@owner", ownerId);
        command.Parameters.AddWithValue("@account", account.Id);
        await command.ExecuteNonQueryAsync();

        var result = OpenAsync(1m);
        await WaitForBlockedSessionsAsync(1);
        try
        {
            // SQL lock timeout is 5s. The first attempt must roll back; the next uses a new connection.
            await Task.Delay(TimeSpan.FromMilliseconds(5300));
            Assert.False(result.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); }
        Assert.NotNull((await result).Receipt);
        Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
    }

    [SqlServerFact]
    public async Task AuditFailure_RollsBackEveryFinancialRowAndDoesNotConsumeTheKey()
    {
        string trigger = "TR_VF_OpeningFault_" + runId;
        int objectId;
        await using var connection = await ConnectAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON [dbo].[FinancialAuditEntries] AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE [UserId] = '{ownerId:D}')
                        THROW 51080, 'Synthetic audit failure for rollback verification.', 1;
                END;
                """;
            await create.ExecuteNonQueryAsync();
            create.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');";
            objectId = Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => OpenAsync(1m));
            Assert.Equal(51080, exception.Number);
            Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
            Assert.False((await service.GetBalanceAsync(Owner(), account.Id)).Balance!.IsInitialized);
        }
        finally
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"""
                IF OBJECT_ID(N'dbo.{trigger}') <> @expected
                   OR NOT EXISTS (SELECT 1 FROM sys.sql_modules WHERE object_id=@expected AND CHARINDEX(@owner, definition)>0)
                    THROW 51081, 'Test trigger ownership changed; cleanup refused.', 1;
                DROP TRIGGER [dbo].[{trigger}];
                """;
            drop.Parameters.AddWithValue("@expected", objectId);
            drop.Parameters.AddWithValue("@owner", ownerId.ToString("D"));
            await drop.ExecuteNonQueryAsync();
        }
        Assert.NotNull((await OpenAsync(1m)).Receipt);
        Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
    }

    [SqlServerFact]
    public async Task RuntimeRole_DeniesAdHocHistoryAccessButAllowsTheCompleteProtocol()
    {
        string principal = "vf_runtime_probe_" + runId;
        byte[] sid;
        await using var administrator = await ConnectAsync();
        await using (var create = administrator.CreateCommand())
        {
            create.CommandText = $"""
                CREATE USER [{principal}] WITHOUT LOGIN;
                ALTER ROLE [VaultFinanceFinancialRuntime] ADD MEMBER [{principal}];
                SELECT [sid] FROM sys.database_principals WHERE [name] = N'{principal}';
                """;
            sid = (byte[])(await create.ExecuteScalarAsync())!;
        }
        try
        {
            await using var runtime = await ConnectAsync();
            await using var command = runtime.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = N'{principal}';";
            await command.ExecuteNonQueryAsync();
            try
            {
                foreach (string denied in new[]
                {
                    "SELECT COUNT(*) FROM dbo.FinancialOperations;",
                    "SELECT COUNT(*) FROM dbo.FinancialAccountMovements;",
                    "DELETE FROM dbo.FinancialRequestOutcomes;",
                    "EXEC dbo.AcquireFinancialAccountLocks @owner, @account;"
                })
                {
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@owner", ownerId);
                    command.Parameters.AddWithValue("@account", account.Id);
                    command.CommandText = denied;
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
                }
                await using var opening = OpeningCommand(runtime, "2.00", "runtime");
                await using (var result = await opening.ExecuteReaderAsync())
                {
                    Assert.True(await result.ReadAsync());
                    Assert.Equal("opening.initialized", result.GetString(0));
                }
                command.CommandText = "EXEC dbo.GetDerivedFinancialBalance @owner, @account;";
                await using var balance = await command.ExecuteReaderAsync();
                Assert.True(await balance.ReadAsync());
                Assert.Equal("balance.read", balance.GetString(0));
                Assert.Equal(2m, balance.GetDecimal(3));
            }
            finally
            {
                command.CommandText = "REVERT;";
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var drop = administrator.CreateCommand();
            drop.CommandText = $"""
                IF DB_NAME() <> N'VaultFinanceJournalTests'
                   OR NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name]=N'{principal}' AND [sid]=@sid AND [type]='S')
                    THROW 51081, 'Test principal ownership changed; cleanup refused.', 1;
                ALTER ROLE [VaultFinanceFinancialRuntime] DROP MEMBER [{principal}];
                DROP USER [{principal}];
                """;
            drop.Parameters.Add("@sid", SqlDbType.VarBinary, 85).Value = sid;
            await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
    }

    private async Task<int[]> WaitForBlockedSessionsAsync(int count)
    {
        await using var observer = await ConnectAsync();
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(int, r.session_id)
            FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            WHERE s.program_name = @application AND r.wait_type LIKE N'LCK_M_%';
            """;
        command.Parameters.AddWithValue("@application", applicationName);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            var ids = new List<int>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) { ids.Add(reader.GetInt32(0)); }
            }
            if (ids.Count >= count) { return ids.ToArray(); }
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Independent SQL requests did not reach the expected lock wait; concurrency was not proved.");
    }
}
