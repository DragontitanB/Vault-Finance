using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.Accounts;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialReversalSqlServerTests
{
    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income, false)]
    [InlineData(FinancialOperationKind.Expense, false)]
    [InlineData(FinancialOperationKind.Transfer, false)]
    [InlineData(FinancialOperationKind.Income, true)]
    [InlineData(FinancialOperationKind.Expense, true)]
    [InlineData(FinancialOperationKind.Transfer, true)]
    public async Task ConcurrentReversals_HaveOneCompensationAndAStableBusinessResult(FinancialOperationKind kind, bool identical)
    {
        var original = await PrepareAsync(kind);
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, source.Id);
        var first = ReverseAsync(original, "race");
        var second = ReverseAsync(original, identical ? "race" : "different-key");
        var both = Task.WhenAll(first, second);
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await both; }
        var results = await both;
        if (identical) { Assert.NotNull(results[0].Receipt); Assert.Equal(results[0], results[1]); }
        else
        {
            Assert.Single(results, x => x.Receipt is not null);
            Assert.Equal(FinancialFailure.AlreadyReversed, Assert.Single(results, x => x.Receipt is null).Failure);
        }
        Assert.Equal(1, await ReversalCountAsync(original));
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(identical ? 4 : 5, (await CountsAsync()).Outcomes);
        Assert.Equal(kind == FinancialOperationKind.Transfer ? 2 : 1,
            await database.FinancialAccountMovements.CountAsync(x => x.UserId == ownerId && x.Kind == FinancialOperationKind.Reversal));
    }

    [SqlServerTheory]
    [InlineData("expense")]
    [InlineData("income")]
    [InlineData("transfer")]
    [InlineData("opposite-transfer")]
    public async Task ReversalAndOrdinaryWriter_ContendAndProduceAValidSerialResult(string scenario)
    {
        var kind = scenario == "expense" ? FinancialOperationKind.Income
            : scenario == "income" ? FinancialOperationKind.Expense : FinancialOperationKind.Transfer;
        var original = await PrepareAsync(kind, scenario == "expense" ? 0m : scenario == "income" ? FinancialAmount.MaximumValue : 100m);
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, source.Id);
        var reversal = ReverseAsync(original);
        Task<FinancialFailure?> ordinary = OrdinaryFailureAsync();
        async Task<FinancialFailure?> OrdinaryFailureAsync()
        {
            if (scenario is "expense" or "income")
                return (await EntryAsync(scenario == "income", source.Id, 20m, "concurrent-entry")).Failure;
            var result = await service.RecordTransferAsync(Owner(),
                new(scenario == "transfer" ? destination.Id : source.Id, scenario == "transfer" ? source.Id : destination.Id,
                    scenario == "transfer" ? 20m : 10m, "USD"), "concurrent-transfer");
            return result.Failure;
        }
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(reversal.IsCompleted); Assert.False(ordinary.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await Task.WhenAll(reversal, ordinary); }
        var result = await reversal; var otherFailure = await ordinary;
        if (scenario == "opposite-transfer")
        {
            Assert.NotNull(result.Receipt); Assert.Null(otherFailure);
            Assert.Equal(90m, await IndependentBalanceAsync(source.Id)); Assert.Equal(10m, await IndependentBalanceAsync(destination.Id));
        }
        else
        {
            Assert.True((result.Receipt is not null) != (otherFailure is null));
            Assert.Equal(scenario == "income" ? FinancialFailure.BalanceLimitExceeded : FinancialFailure.InsufficientFunds, result.Failure ?? otherFailure);
            Assert.Equal(scenario == "income" ? FinancialAmount.MaximumValue : scenario == "expense" ? 0m : 100m, await IndependentBalanceAsync(source.Id));
            Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
        }
        Assert.Equal(result.Receipt is null ? 0 : 1, await ReversalCountAsync(original));
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income, true, false)]
    [InlineData(FinancialOperationKind.Income, true, true)]
    [InlineData(FinancialOperationKind.Expense, true, false)]
    [InlineData(FinancialOperationKind.Expense, true, true)]
    [InlineData(FinancialOperationKind.Transfer, true, false)]
    [InlineData(FinancialOperationKind.Transfer, true, true)]
    [InlineData(FinancialOperationKind.Transfer, false, false)]
    [InlineData(FinancialOperationKind.Transfer, false, true)]
    public async Task ArchiveRace_CompensatesExactlyInBothControlledOrdersWithoutReactivation(FinancialOperationKind kind, bool archiveSource, bool reversalFirst)
    {
        var original = await PrepareAsync(kind);
        var account = archiveSource ? source : destination;
        await using var archiveScope = fixture.Provider.CreateAsyncScope();
        var archiveContext = archiveScope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        archiveContext.Database.SetConnectionString(connectionString);
        var archiveService = new AccountService(archiveScope.ServiceProvider.GetRequiredService<IAccountStore>());
        if (!reversalFirst)
        {
            await using var transaction = await archiveContext.Database.BeginTransactionAsync();
            Assert.Null((await archiveService.ArchiveAsync(ownerId, account.Id, CancellationToken.None)).Failure);
            var waiting = ReverseAsync(original);
            try { await WaitForBlockedSessionsAsync(1); Assert.False(waiting.IsCompleted); }
            finally { await transaction.CommitAsync(); }
            Assert.NotNull((await waiting).Receipt);
        }
        else
        {
            await using var gate = await CreateAuditGateAsync();
            Task<FinancialReversalOutcome>? reversal = null; Task<AccountOutcome>? archive = null;
            try
            {
                reversal = ReverseAsync(original);
                await WaitForBlockedSessionsAsync(1, gate.SessionId);
                archive = archiveService.ArchiveAsync(ownerId, account.Id, CancellationToken.None);
                Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            }
            finally
            {
                await gate.ReleaseAsync();
                var pending = new List<Task>();
                if (reversal is not null) pending.Add(reversal);
                if (archive is not null) pending.Add(archive);
                await Task.WhenAll(pending);
            }
            Assert.NotNull((await reversal!).Receipt); Assert.Null((await archive!).Failure);
        }
        Assert.Equal("Archived", (await archiveService.GetAsync(ownerId, account.Id, CancellationToken.None)).Account!.Status);
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(1, await ReversalCountAsync(original));
    }

    private async Task HoldAccountAsync(SqlConnection connection, SqlTransaction transaction, Guid id)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "EXEC dbo.AcquireFinancialAccountLocks @owner,@account;";
        command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@account", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int[]> WaitForBlockedSessionsAsync(int count, int? directBlocker = null)
    {
        await using var observer = await ConnectAsync();
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(int,r.session_id) FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
            WHERE s.program_name=@application AND r.wait_type LIKE N'LCK_M_%'
              AND (@blocker IS NULL OR r.blocking_session_id=@blocker);
            """;
        command.Parameters.AddWithValue("@application", applicationName);
        command.Parameters.Add("@blocker", SqlDbType.Int).Value = (object?)directBlocker ?? DBNull.Value;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            var sessions = new List<int>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) sessions.Add(reader.GetInt32(0));
            if (sessions.Count >= count) return sessions.ToArray();
            await Task.Delay(25);
        }
        command.CommandText = """
            SELECT CONCAT(r.session_id,':',r.blocking_session_id,':',r.wait_type,':',r.wait_resource)
            FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=r.session_id
            WHERE s.program_name=@application;
            """;
        var observed = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) observed.Add(reader.GetString(0));
        throw new InvalidOperationException("Expected independent SQL lock contention was not observed. session:blocker:wait:resource = " + string.Join("; ", observed));
    }

    private async Task<AuditGate> CreateAuditGateAsync()
    {
        string resource = "VaultFinance.ReversalGate." + runId;
        string trigger = "TR_VF_ReversalGate_" + runId;
        var connection = await ConnectAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=1000; SELECT CASE WHEN @r>=0 THEN @@SPID ELSE -1 END;";
            command.Parameters.AddWithValue("@resource", resource);
            int session = Convert.ToInt32(await command.ExecuteScalarAsync()); Assert.True(session > 0);
            command.Parameters.Clear();
            command.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.FinancialAuditEntries AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND Action='financial.reversal.posted')
                    BEGIN
                        DECLARE @r int;
                        EXEC @r=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=5000;
                        IF @r<0 THROW 51080,'Synthetic reversal gate timed out.',1;
                    END;
                END;
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');";
            int objectId = Convert.ToInt32(await command.ExecuteScalarAsync());
            return new(connection, trigger, resource, objectId, session);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class AuditGate(SqlConnection connection, string trigger, string resource, int objectId, int sessionId) : IAsyncDisposable
    {
        public int SessionId => sessionId;
        private bool released;
        public async Task ReleaseAsync()
        {
            if (released) return;
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';";
            command.Parameters.AddWithValue("@resource", resource); await command.ExecuteNonQueryAsync(); released = true;
        }
        public async ValueTask DisposeAsync()
        {
            try { await ReleaseAsync(); await RemoveOwnedTriggerAsync(connection, trigger, objectId, resource); }
            finally { await connection.DisposeAsync(); }
        }
    }

    private static async Task RemoveOwnedTriggerAsync(SqlConnection connection, string name, int id, string marker)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_NAME()<>N'VaultFinanceJournalTests' OR OBJECT_ID(N'dbo.{name}')<>@id
                OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=@id AND CHARINDEX(@marker,definition)>0)
                THROW 51081,'Test trigger ownership changed; cleanup refused.',1;
            DROP TRIGGER [dbo].[{name}];
            """;
        command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@marker", marker);
        await command.ExecuteNonQueryAsync();
    }
}

