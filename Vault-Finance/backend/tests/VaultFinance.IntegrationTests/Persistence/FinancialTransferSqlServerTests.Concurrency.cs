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

public sealed partial class FinancialTransferSqlServerTests
{
    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentTransfers_UseIndependentContendingSessionsWithoutOverspendingOrDuplication(bool identical)
    {
        await OpenAsync(source, 10000m); await OpenAsync(destination, 0m);
        var third = await AddAccountAsync(); await OpenAsync(third, 0m);
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, source.Id);
        var first = TransferAsync(7000m, "race");
        var second = TransferAsync(7000m, identical ? "race" : "race-2", to: identical ? destination.Id : third.Id);
        var both = Task.WhenAll(first, second);
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await both; }
        var results = await both;
        if (identical)
        {
            Assert.NotNull(results[0].Receipt); Assert.Equal(results[0], results[1]);
        }
        else
        {
            Assert.Single(results, x => x.Receipt is not null);
            Assert.Equal(FinancialFailure.InsufficientFunds, Assert.Single(results, x => x.Receipt is null).Failure);
        }
        Assert.Equal(3000m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(7000m, await IndependentTotalAsync(destination.Id, third.Id));
        Assert.Equal(1, await TransferOperationCountAsync());
        Assert.Equal(2, await database.FinancialAccountMovements.CountAsync(x => x.UserId == ownerId && x.Kind == FinancialOperationKind.Transfer));
    }

    [SqlServerFact]
    public async Task OppositeDirections_BlockOnTheSameFirstSqlGuidBeforeAcquiringTheSecondKey()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 100m);
        await using var blocker = await ConnectAsync();
        Guid first;
        await using (var order = blocker.CreateCommand())
        {
            order.CommandText = "SELECT CASE WHEN @a<@b THEN @a ELSE @b END;";
            order.Parameters.AddWithValue("@a", source.Id); order.Parameters.AddWithValue("@b", destination.Id);
            first = (Guid)(await order.ExecuteScalarAsync())!;
        }
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, first);
        var forward = TransferAsync(10m, "forward");
        var backward = TransferAsync(10m, "backward", from: destination.Id, to: source.Id);
        var both = Task.WhenAll(forward, backward);
        try
        {
            var sessions = await WaitForBlockedSessionsAsync(2);
            Assert.Equal(2, sessions.Distinct().Count());
            await using var observer = await ConnectAsync();
            await using var locks = observer.CreateCommand();
            // Neither request may hold an Accounts U/X key while the globally first key is unavailable.
            locks.CommandText = """
                SELECT COUNT(*) FROM sys.dm_tran_locks l
                JOIN sys.partitions p ON p.hobt_id=l.resource_associated_entity_id
                WHERE p.object_id=OBJECT_ID(N'dbo.Accounts') AND l.resource_type='KEY'
                  AND l.request_session_id IN (@s1,@s2) AND l.request_status='GRANT'
                  AND l.request_mode IN ('U','X','RangeS-U','RangeX-X');
                """;
            locks.Parameters.AddWithValue("@s1", sessions[0]); locks.Parameters.AddWithValue("@s2", sessions[1]);
            Assert.Equal(0, Convert.ToInt32(await locks.ExecuteScalarAsync()));
            Assert.False(both.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await both; }
        Assert.All(await both, x => Assert.NotNull(x.Receipt));
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(100m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(200m, await IndependentTotalAsync(source.Id, destination.Id));
        Assert.Equal(2, await TransferOperationCountAsync());
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferAndSingleAccountWriter_SerializeFundsOrDestinationHeadroom(bool income)
    {
        await OpenAsync(source, income ? 1m : 10000m);
        await OpenAsync(destination, income ? FinancialAmount.MaximumValue - 1m : 0m);
        Guid sharedAccount = income ? destination.Id : source.Id;
        decimal amount = income ? 1m : 7000m;
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, sharedAccount);
        var transfer = TransferAsync(amount, "race-transfer");
        var entry = income
            ? service.RecordIncomeAsync(Owner(), new(sharedAccount, amount, "USD"), "race-entry")
            : service.RecordExpenseAsync(Owner(), new(sharedAccount, amount, "USD"), "race-entry");
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(transfer.IsCompleted); Assert.False(entry.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await Task.WhenAll(transfer, entry); }
        var t = await transfer; var e = await entry;
        Assert.Equal(1, (t.Receipt is null ? 0 : 1) + (e.Receipt is null ? 0 : 1));
        Assert.Equal(income ? FinancialFailure.BalanceLimitExceeded : FinancialFailure.InsufficientFunds, t.Failure ?? e.Failure);
        Assert.Equal(income ? FinancialAmount.MaximumValue : 3000m, await IndependentBalanceAsync(sharedAccount));
        Assert.Equal(income ? (t.Receipt is not null ? 0m : 1m) : (t.Receipt is not null ? 7000m : 0m),
            await IndependentBalanceAsync(income ? source.Id : destination.Id));
        Assert.Equal(t.Receipt is null ? 0 : 1, await TransferOperationCountAsync());
    }

    [SqlServerTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ArchiveRace_UsesTheRealEfPathAndControlsBothPossibleOrders(bool archiveSource, bool transferFirst)
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var account = archiveSource ? source : destination;
        await using var archiveScope = fixture.Provider.CreateAsyncScope();
        var archiveContext = archiveScope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        archiveContext.Database.SetConnectionString(connectionString);
        var archiveService = new AccountService(archiveScope.ServiceProvider.GetRequiredService<IAccountStore>());
        if (!transferFirst)
        {
            await using var transaction = await archiveContext.Database.BeginTransactionAsync();
            Assert.Null((await archiveService.ArchiveAsync(ownerId, account.Id, CancellationToken.None)).Failure);
            var waiting = TransferAsync(10m);
            try { await WaitForBlockedSessionsAsync(1); Assert.False(waiting.IsCompleted); }
            finally { await transaction.CommitAsync(); }
            Assert.Equal(FinancialFailure.Archived, (await waiting).Failure);
        }
        else
        {
            await using var gate = await CreateAuditGateAsync();
            Task<FinancialTransferOutcome>? transfer = null;
            Task<AccountOutcome>? archive = null;
            try
            {
                transfer = TransferAsync(10m);
                await WaitForBlockedSessionsAsync(1, gate.SessionId);
                archive = archiveService.ArchiveAsync(ownerId, account.Id, CancellationToken.None);
                Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            }
            finally
            {
                await gate.ReleaseAsync();
                if (transfer is not null) await transfer;
                if (archive is not null) await archive;
            }
            Assert.NotNull((await transfer!).Receipt);
            Assert.Null((await archive!).Failure);
        }
        Assert.Equal("Archived", (await archiveService.GetAsync(ownerId, account.Id, CancellationToken.None)).Account!.Status);
        Assert.Equal(transferFirst ? 90m : 100m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(transferFirst ? 10m : 0m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(100m, await IndependentTotalAsync(source.Id, destination.Id));
        Assert.Equal(transferFirst ? 1 : 0, await TransferOperationCountAsync());
    }

    [SqlServerFact]
    public async Task IndependentPairs_RunOnConcurrentSessionsAndCommitWithoutCrossAccountEffects()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var a = await AddAccountAsync(); var b = await AddAccountAsync();
        await OpenAsync(a, 100m); await OpenAsync(b, 0m);
        await using var gate = await CreateAuditGateAsync();
        var first = TransferAsync(10m, "pair-one");
        var second = TransferAsync(20m, "pair-two", from: a.Id, to: b.Id);
        var both = Task.WhenAll(first, second);
        try
        {
            // A pair can also wait on the other's uncommitted journal index rows under READ COMMITTED.
            // Assert actual concurrent requests and valid isolated effects, not an unsupported throughput guarantee.
            await WaitForBlockedSessionsAsync(1, gate.SessionId);
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally { await gate.ReleaseAsync(); await both; }
        Assert.All(await both, x => Assert.NotNull(x.Receipt));
        Assert.Equal(90m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(10m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(80m, await IndependentBalanceAsync(a.Id));
        Assert.Equal(20m, await IndependentBalanceAsync(b.Id));
        Assert.Equal(100m, await IndependentTotalAsync(source.Id, destination.Id));
        Assert.Equal(100m, await IndependentTotalAsync(a.Id, b.Id));
        Assert.Equal(2, await TransferOperationCountAsync());
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
        string resource = "VaultFinance.TransferGate." + runId;
        string trigger = "TR_VF_TransferGate_" + runId;
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
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND Action='financial.transfer.posted')
                    BEGIN
                        DECLARE @r int;
                        EXEC @r=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=5000;
                        IF @r<0 THROW 51080,'Synthetic transfer gate timed out.',1;
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
