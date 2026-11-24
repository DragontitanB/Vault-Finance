using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.Accounts;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class OpeningCorrectionSqlServerTests
{
    [SqlServerFact]
    public async Task BalanceRead_WaitsThroughTheNegativeUncommittedCompensationPrefix()
    {
        await OpenAsync(source, 100m); await EntryAsync(false, 80m, "expense");
        await using var gate = await CreateAuditGateAsync(compensation: true);
        Task<OpeningCorrectionOutcome>? correction = null; Task<BalanceOutcome>? balance = null;
        try
        {
            correction = CorrectAsync(80m);
            await WaitForBlockedSessionsAsync(1, gate.SessionId);
            balance = service.GetBalanceAsync(Owner(), source.Id);
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(correction.IsCompleted); Assert.False(balance.IsCompleted);
        }
        finally
        {
            await gate.ReleaseAsync(); var pending = new List<Task>();
            if (correction is not null) pending.Add(correction); if (balance is not null) pending.Add(balance);
            await Task.WhenAll(pending);
        }
        Assert.NotNull((await correction!).Receipt); Assert.Null((await balance!).Failure);
        Assert.Equal(0m, (await balance).Balance!.Amount); Assert.Equal(0m, await IndependentBalanceAsync(source.Id));
    }

    [SqlServerTheory]
    [InlineData("different-keys")]
    [InlineData("identical")]
    [InlineData("changed-payload")]
    public async Task ConcurrentCorrections_SerializeTheExpectedRevisionAndDoNotFork(string scenario)
    {
        await OpenAsync(source, 100m);
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, source.Id);
        var first = CorrectAsync(80m, "race");
        var second = CorrectAsync(scenario == "identical" ? 80m : 120m, scenario == "different-keys" ? "other-key" : "race");
        var both = Task.WhenAll(first, second);
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(both.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await both; }
        var results = await both;
        if (scenario == "identical") { Assert.NotNull(results[0].Receipt); Assert.Equal(results[0], results[1]); }
        else
        {
            Assert.Single(results, x => x.Receipt is not null);
            Assert.Equal(scenario == "different-keys" ? FinancialFailure.OpeningVersionConflict : FinancialFailure.IdempotencyConflict,
                Assert.Single(results, x => x.Receipt is null).Failure);
        }
        var receipt = results.First(x => x.Receipt is not null).Receipt!;
        await AssertCorrectionBatchAsync(receipt);
        Assert.Equal(receipt.OpeningAmount, await IndependentBalanceAsync(source.Id));
        Assert.Equal(2, await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId && x.AccountId == source.Id));
        Assert.Equal(1, await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId && x.AccountId == source.Id && x.PreviousRevision == 1));
    }

    [SqlServerTheory]
    [InlineData("expense")]
    [InlineData("income")]
    [InlineData("transfer")]
    [InlineData("reversal")]
    public async Task CorrectionAndOtherWriter_ContendAndMatchAnExactSerialResult(string scenario)
    {
        await OpenAsync(source, scenario == "income" ? VaultFinance.Domain.FinancialOperations.FinancialAmount.MaximumValue : scenario == "reversal" ? 0m : 100m);
        await OpenAsync(destination, 0m);
        Guid? original = scenario == "reversal" ? (await EntryAsync(true, 20m, "original-income")).Receipt!.OperationId : null;
        decimal desired = scenario == "income" ? VaultFinance.Domain.FinancialOperations.FinancialAmount.MaximumValue - 20m : scenario == "reversal" ? 10m : 80m;
        await using var blocker = await ConnectAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await HoldAccountAsync(blocker, transaction, source.Id);
        var correction = CorrectAsync(desired);
        async Task<FinancialFailure?> OtherAsync()
        {
            return scenario switch
            {
                "expense" => (await EntryAsync(false, 90m, "race-entry")).Failure,
                "income" => (await EntryAsync(true, 20m, "race-entry")).Failure,
                "transfer" => (await service.RecordTransferAsync(Owner(), new(source.Id, destination.Id, 90m, "USD"), "race-transfer")).Failure,
                _ => (await service.RecordReversalAsync(Owner(), new(original!.Value, "Synthetic race"), "race-reversal")).Failure
            };
        }
        var other = OtherAsync();
        try
        {
            Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            Assert.False(correction.IsCompleted); Assert.False(other.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await Task.WhenAll(correction, other); }
        var c = await correction; var failure = await other;
        if (scenario is "expense" or "transfer")
        {
            Assert.True((c.Receipt is not null) != (failure is null));
            Assert.Equal(FinancialFailure.InsufficientFunds, c.Failure ?? failure);
            Assert.Equal(c.Receipt is not null ? 80m : 10m, await IndependentBalanceAsync(source.Id));
            Assert.Equal(scenario == "transfer" && failure is null ? 90m : 0m, await IndependentBalanceAsync(destination.Id));
        }
        else if (scenario == "income")
        {
            Assert.NotNull(c.Receipt);
            Assert.True(failure is null or FinancialFailure.BalanceLimitExceeded);
            Assert.Equal(desired + (failure is null ? 20m : 0m), await IndependentBalanceAsync(source.Id));
        }
        else
        {
            Assert.NotNull(c.Receipt); Assert.Null(failure);
            Assert.Equal(10m, await IndependentBalanceAsync(source.Id));
        }
        if (c.Receipt is not null) await AssertCorrectionBatchAsync(c.Receipt);
        Assert.Equal(c.Receipt is null ? 1 : 2, await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId && x.AccountId == source.Id));
    }

    [SqlServerTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ArchiveRace_UsesTheActualEfPathAndRespectsTheZeroCancellationException(bool zero, bool correctionFirst)
    {
        await OpenAsync(source, 100m);
        await using var archiveScope = fixture.Provider.CreateAsyncScope();
        var archiveContext = archiveScope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        archiveContext.Database.SetConnectionString(connectionString);
        var archiveService = new AccountService(archiveScope.ServiceProvider.GetRequiredService<IAccountStore>());
        OpeningCorrectionOutcome result;
        if (!correctionFirst)
        {
            await using var transaction = await archiveContext.Database.BeginTransactionAsync();
            Assert.Null((await archiveService.ArchiveAsync(ownerId, source.Id, CancellationToken.None)).Failure);
            var waiting = CorrectAsync(zero ? 0m : 80m);
            try { await WaitForBlockedSessionsAsync(1); Assert.False(waiting.IsCompleted); }
            finally { await transaction.CommitAsync(); }
            result = await waiting;
            Assert.Equal(zero ? null : FinancialFailure.Archived, result.Failure);
        }
        else
        {
            await using var gate = await CreateAuditGateAsync();
            Task<OpeningCorrectionOutcome>? correction = null; Task<AccountOutcome>? archive = null;
            try
            {
                correction = CorrectAsync(zero ? 0m : 80m);
                await WaitForBlockedSessionsAsync(1, gate.SessionId);
                archive = archiveService.ArchiveAsync(ownerId, source.Id, CancellationToken.None);
                Assert.Equal(2, (await WaitForBlockedSessionsAsync(2)).Distinct().Count());
            }
            finally
            {
                await gate.ReleaseAsync(); var pending = new List<Task>();
                if (correction is not null) pending.Add(correction); if (archive is not null) pending.Add(archive);
                await Task.WhenAll(pending);
            }
            result = await correction!; Assert.NotNull(result.Receipt); Assert.Null((await archive!).Failure);
        }
        Assert.Equal("Archived", (await archiveService.GetAsync(ownerId, source.Id, CancellationToken.None)).Account!.Status);
        Assert.Equal(result.Receipt?.OpeningAmount ?? 100m, await IndependentBalanceAsync(source.Id));
        if (result.Receipt is not null) await AssertCorrectionBatchAsync(result.Receipt);
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

    private async Task<AuditGate> CreateAuditGateAsync(bool compensation = false)
    {
        string resource = "VaultFinance.CorrectionGate." + runId;
        string trigger = "TR_VF_CorrectionGate_" + runId;
        var connection = await ConnectAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=1000; SELECT CASE WHEN @r>=0 THEN @@SPID ELSE -1 END;";
            command.Parameters.AddWithValue("@resource", resource);
            int session = Convert.ToInt32(await command.ExecuteScalarAsync()); Assert.True(session > 0);
            command.Parameters.Clear();
            string table = compensation ? "FinancialOperations" : "FinancialAuditEntries";
            string filter = compensation ? "Kind=5 AND OriginalOperationKind=1" : "Action='financial.opening_correction.posted'";
            command.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.[{table}] AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{ownerId:D}' AND {filter})
                    BEGIN
                        DECLARE @r int;
                        EXEC @r=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=5000;
                        IF @r<0 THROW 51080,'Synthetic correction gate timed out.',1;
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

