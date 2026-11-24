using System.Data;
using Microsoft.Data.SqlClient;
using VaultFinance.Application.FinancialInspection;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed partial class FinancialInspectionSqlServerTests
{
    [SqlServerTheory]
    [InlineData("income")]
    [InlineData("expense")]
    [InlineData("transfer")]
    [InlineData("correction")]
    public async Task Capture_RetainsSharedTableLocksAndExcludesConcurrentWritesFromTheSameSnapshot(string command)
    {
        await Open(a, 100m); await Open(b, 0m);
        await using var blocker = await Connect(); await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await Hold(blocker, transaction, "FinancialAuditEntries");
        var capture = reader.CaptureAsync(owner, CancellationToken.None);
        var readers = await WaitForBlocked(1, ".reader");
        await using (var observer = await Connect())
        await using (var locks = observer.CreateCommand())
        {
            locks.CommandText = """
                SELECT COUNT(DISTINCT resource_associated_entity_id) FROM sys.dm_tran_locks
                WHERE request_session_id=@session AND resource_type='OBJECT' AND request_status='GRANT' AND request_mode='S'
                  AND resource_database_id=DB_ID() AND resource_associated_entity_id IN (
                    OBJECT_ID(N'dbo.Accounts'),OBJECT_ID(N'dbo.FinancialPostingBatches'),OBJECT_ID(N'dbo.FinancialOperations'),
                    OBJECT_ID(N'dbo.FinancialOpeningVersions'),OBJECT_ID(N'dbo.FinancialRequestOutcomes'));
                """;
            locks.Parameters.AddWithValue("@session", readers.Single()); Assert.Equal(5, Convert.ToInt32(await locks.ExecuteScalarAsync()));
        }
        var writing = Write(command);
        try
        {
            Assert.Equal(2, (await WaitForBlocked(2)).Distinct().Count()); Assert.False(capture.IsCompleted); Assert.False(writing.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); await Task.WhenAll(capture, writing); }
        var result = await capture; Assert.Null(result.ErrorCode); Assert.NotNull(result.Snapshot);
        Assert.Equal(100m, result.Snapshot.ReportedBalances.Single(x => x.AccountId == a.Id).Amount);
        Assert.Equal(0m, result.Snapshot.ReportedBalances.Single(x => x.AccountId == b.Id).Amount);
        Assert.DoesNotContain(result.Snapshot.Operations, x => x.Id == writing.Result);
        Assert.Equal(InspectionStatus.Healthy, new JournalIntegrityEvaluator().Evaluate(result.Snapshot).Status);
        Assert.Equal(InspectionStatus.Healthy, (await inspection.InspectAsync(Authorized())).Status);
    }

    [SqlServerTheory]
    [InlineData("income")]
    [InlineData("expense")]
    [InlineData("transfer")]
    [InlineData("correction")]
    public async Task Capture_WaitsForInFlightWriterAndNeverReportsAPartialBatchAsCorruption(string command)
    {
        await Open(a, 100m); await Open(b, 0m);
        if (command == "correction") await writer.RecordExpenseAsync(Owner(), new(a.Id, 80m, "USD"), "earlier-expense");
        string trigger = "TR_VF_InspectionGate_" + run; string resource = "VaultFinance.InspectionGate." + run;
        await using var gate = await Connect(); int id;
        await using (var hold = gate.CreateCommand())
        {
            hold.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=1000; SELECT @r;";
            hold.Parameters.AddWithValue("@resource", resource); Assert.True(Convert.ToInt32(await hold.ExecuteScalarAsync()) >= 0);
        }
        await using (var create = gate.CreateCommand())
        {
            string table = command == "correction" ? "FinancialOperations" : "FinancialAuditEntries";
            string filter = command == "correction" ? "Kind=5 AND OriginalOperationKind=1" : $"Action='financial.{command}.posted'";
            create.CommandText = $"""
                CREATE TRIGGER [dbo].[{trigger}] ON dbo.[{table}] AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted WHERE UserId='{owner:D}' AND {filter})
                    BEGIN
                        DECLARE @r int;
                        EXEC @r=sys.sp_getapplock @Resource=N'{resource}',@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=5000;
                        IF @r<0 THROW 51080,'Synthetic inspection gate timed out.',1;
                    END;
                END;
                """;
            await create.ExecuteNonQueryAsync(); create.CommandText = $"SELECT OBJECT_ID(N'dbo.{trigger}');"; id = Convert.ToInt32(await create.ExecuteScalarAsync());
        }
        Task<Guid>? writing = null; Task<JournalCapture>? capture = null;
        try
        {
            writing = Write(command); await WaitForBlocked(1, ".writer");
            capture = reader.CaptureAsync(owner, CancellationToken.None);
            Assert.Equal(2, (await WaitForBlocked(2)).Distinct().Count()); Assert.False(writing.IsCompleted); Assert.False(capture.IsCompleted);
        }
        finally
        {
            await using var release = gate.CreateCommand(); release.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';";
            release.Parameters.AddWithValue("@resource", resource); await release.ExecuteNonQueryAsync();
            try
            {
                var tasks = new List<Task>(); if (writing is not null) tasks.Add(writing); if (capture is not null) tasks.Add(capture); await Task.WhenAll(tasks);
            }
            finally
            {
                await using var drop = gate.CreateCommand();
                drop.CommandText = $"""
                    IF DB_NAME()<>N'VaultFinanceJournalTests' OR OBJECT_ID(N'dbo.{trigger}')<>@id
                       OR NOT EXISTS(SELECT 1 FROM sys.sql_modules WHERE object_id=@id AND CHARINDEX(@marker,definition)>0)
                        THROW 51081,'Inspection test trigger ownership changed; cleanup refused.',1;
                    DROP TRIGGER [dbo].[{trigger}];
                    """;
                drop.Parameters.AddWithValue("@id", id); drop.Parameters.AddWithValue("@marker", resource); await drop.ExecuteNonQueryAsync();
            }
        }
        var result = await capture!; Assert.Null(result.ErrorCode); Assert.Contains(result.Snapshot!.Operations, x => x.Id == writing!.Result);
        var report = new JournalIntegrityEvaluator().Evaluate(result.Snapshot);
        Assert.True(report.Status == InspectionStatus.Healthy, string.Join(",", report.Findings.Select(x => x.Code)));
        Assert.Equal(command == "income" ? 120m : command == "correction" ? 0m : 80m, result.Snapshot.ReportedBalances.Single(x => x.AccountId == a.Id).Amount);
    }

    private async Task<Guid> Write(string command)
    {
        if (command == "income")
        { var result = await writer.RecordIncomeAsync(Owner(), new(a.Id, 20m, "USD"), "concurrent-income"); Assert.Null(result.Failure); return result.Receipt!.OperationId; }
        if (command == "expense")
        { var result = await writer.RecordExpenseAsync(Owner(), new(a.Id, 20m, "USD"), "concurrent-expense"); Assert.Null(result.Failure); return result.Receipt!.OperationId; }
        if (command == "transfer")
        { var result = await writer.RecordTransferAsync(Owner(), new(a.Id, b.Id, 20m, "USD"), "concurrent-transfer"); Assert.Null(result.Failure); return result.Receipt!.OperationId; }
        var corrected = await writer.CorrectOpeningAsync(Owner(), new(a.Id, 80m, "USD", 1, "Synthetic concurrent correction"), "concurrent-correction");
        Assert.Null(corrected.Failure); return corrected.Receipt!.OpeningOperationId!.Value;
    }
}
