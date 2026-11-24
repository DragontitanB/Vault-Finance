using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Application.FinancialInspection;
using VaultFinance.Domain.Accounts;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.IntegrationTests.Persistence;

[Collection(FinancialSqlProtocolCollection.Name)]
public sealed partial class FinancialInspectionSqlServerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly string run = Guid.NewGuid().ToString("N");
    private AsyncServiceScope scope;
    private VaultFinanceDbContext database = null!;
    private VaultFinanceDbContext writerContext = null!;
    private VaultFinanceDbContext readerContext = null!;
    private FinancialAccountService writer = null!;
    private SqlJournalInspectionReader reader = null!;
    private FinancialInspectionService inspection = null!;
    private Account a = null!;
    private Account b = null!;
    private string connectionString = null!;
    private string prefix = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") return;
        scope = fixture.Provider.CreateAsyncScope(); database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        a = new(Guid.NewGuid(), owner, "Synthetic inspection A", "USD"); b = new(Guid.NewGuid(), owner, "Synthetic inspection B", "USD");
        database.Users.Add(new ApplicationUser { Id = owner, UserName = $"inspection-{owner:N}@example.invalid", Email = $"inspection-{owner:N}@example.invalid" });
        database.AddRange(a, b); await database.SaveChangesAsync(); prefix = "VaultFinance.InspectionTests." + run;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString()) { Pooling = false, ApplicationName = prefix + ".writer" };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        writerContext = SqlTestDatabase.CreateContext(connectionString); writer = new(new SqlFinancialJournal(writerContext));
        target.ApplicationName = prefix + ".reader"; readerContext = SqlTestDatabase.CreateContext(target.ConnectionString);
        reader = new(readerContext); inspection = new(reader);
    }
    public async Task DisposeAsync()
    {
        if (database is null) return;
        await writerContext.DisposeAsync(); await readerContext.DisposeAsync(); await scope.DisposeAsync();
        // Only valid synthetic histories are inserted by this suite and retained. Corruption lives in pure fixtures.
    }
    private FinancialInspectionContext Authorized() => new(Guid.NewGuid(), owner, true);
    private FinancialOwner Owner() => new(owner);
    private async Task<OpeningReceipt> Open(Account account, decimal amount)
    {
        var result = await writer.InitializeOpeningAsync(Owner(), new(account.Id, amount, account.Currency, "Synthetic opening"), "open-" + account.Id.ToString("N"));
        Assert.Null(result.Failure); return Assert.IsType<OpeningReceipt>(result.Receipt);
    }

    [SqlServerTheory]
    [InlineData("uninitialized")]
    [InlineData("zero")]
    [InlineData("positive")]
    [InlineData("entries")]
    [InlineData("transfer-reversals")]
    [InlineData("corrections")]
    [InlineData("negative-prefix")]
    [InlineData("archived-cancellation")]
    [InlineData("rejections")]
    [InlineData("maximum")]
    public async Task ValidSqlHistories_AgreeWithTheActualUsualReaderAndInspectionNeverWrites(string scenario)
    {
        if (scenario != "uninitialized") await Open(a, scenario is "zero" or "rejections" ? 0m : scenario == "maximum" ? VaultFinance.Domain.FinancialOperations.FinancialAmount.MaximumValue : 100m);
        if (scenario == "entries")
        {
            Assert.NotNull((await writer.RecordIncomeAsync(Owner(), new(a.Id, 20m, "USD", "Synthetic \u03b1\U0001F4B0"), "income")).Receipt);
            Assert.NotNull((await writer.RecordExpenseAsync(Owner(), new(a.Id, 30m, "USD"), "expense")).Receipt);
        }
        if (scenario == "transfer-reversals")
        {
            await Open(b, 0m);
            var income = (await writer.RecordIncomeAsync(Owner(), new(a.Id, 20m, "USD"), "income")).Receipt!;
            var expense = (await writer.RecordExpenseAsync(Owner(), new(a.Id, 10m, "USD"), "expense")).Receipt!;
            var transfer = (await writer.RecordTransferAsync(Owner(), new(a.Id, b.Id, 30m, "USD"), "transfer")).Receipt!;
            foreach (var id in new[] { income.OperationId, expense.OperationId, transfer.OperationId })
                Assert.NotNull((await writer.RecordReversalAsync(Owner(), new(id, "Synthetic reversal"), "reverse-" + id.ToString("N"))).Receipt);
            a.Archive(); b.Archive(); await database.SaveChangesAsync();
        }
        if (scenario == "corrections")
        {
            int version = 1;
            foreach (var amount in new[] { 80m, 0m, 20m, 50m })
                Assert.NotNull((await writer.CorrectOpeningAsync(Owner(), new(a.Id, amount, "USD", version, "Synthetic correction"), "correct-" + version++)).Receipt);
        }
        if (scenario == "negative-prefix")
        {
            await writer.RecordExpenseAsync(Owner(), new(a.Id, 80m, "USD"), "expense");
            Assert.NotNull((await writer.CorrectOpeningAsync(Owner(), new(a.Id, 80m, "USD", 1, "Synthetic correction"), "correct")).Receipt);
        }
        if (scenario == "archived-cancellation")
        {
            a.Archive(); await database.SaveChangesAsync();
            Assert.NotNull((await writer.CorrectOpeningAsync(Owner(), new(a.Id, 0m, "USD", 1, "Synthetic cancellation"), "correct")).Receipt);
        }
        if (scenario == "rejections")
        {
            await writer.RecordExpenseAsync(Owner(), new(a.Id, 1m, "USD"), "funds");
            await writer.RecordIncomeAsync(Owner(), new(b.Id, 1m, "USD"), "uninitialized");
            await writer.CorrectOpeningAsync(Owner(), new(a.Id, 0m, "USD", 1, "Synthetic no-change"), "no-change");
            await writer.RecordTransferAsync(Owner(), new(a.Id, Guid.NewGuid(), 1m, "USD"), "missing");
            await writer.RecordReversalAsync(Owner(), new(Guid.NewGuid(), "Synthetic missing"), "missing-reversal");
        }
        var before = await OwnerRows(); var report = await inspection.InspectAsync(Authorized());
        Assert.True(report.Status == InspectionStatus.Healthy, report.ErrorCode + ": " + string.Join(",", report.Findings.Select(x => x.Code)));
        Assert.Empty(report.Findings); Assert.Equal(2, report.Accounts.Count);
        Assert.Equal(scenario != "uninitialized", report.Accounts.Single(x => x.AccountId == a.Id).IsInitialized);
        Assert.Equal(before, await OwnerRows());
        var captured = await reader.CaptureAsync(owner, CancellationToken.None); Assert.Null(captured.ErrorCode);
        Assert.Equal("shared-table-locks-v1", captured.Snapshot!.ConsistencyMode); Assert.True(captured.Snapshot.IsComplete);
        foreach (var account in new[] { a, b })
        {
            var usual = (await writer.GetBalanceAsync(Owner(), account.Id)).Balance!;
            var observed = Assert.Single(captured.Snapshot.ReportedBalances, x => x.AccountId == account.Id);
            Assert.Equal(usual.IsInitialized, observed.IsInitialized); Assert.Equal(usual.Amount, observed.Amount);
        }
        Assert.Equal(before, await OwnerRows());
    }

    [SqlServerFact]
    public async Task OwnerScope_IsExplicitAndOrdinaryContextIsForbidden()
    {
        await Open(a, 100m);
        var otherId = Guid.NewGuid(); var other = new Account(Guid.NewGuid(), otherId, "Synthetic other owner", "DOP");
        database.Users.Add(new ApplicationUser { Id = otherId, UserName = $"other-{otherId:N}@example.invalid", Email = $"other-{otherId:N}@example.invalid" });
        database.Add(other); await database.SaveChangesAsync();
        Assert.NotNull((await writer.InitializeOpeningAsync(new(otherId), new(other.Id, 500m, "DOP", "Synthetic"), "open")).Receipt);
        var report = await inspection.InspectAsync(Authorized()); Assert.Equal(InspectionStatus.Healthy, report.Status);
        Assert.DoesNotContain(report.Accounts, x => x.AccountId == other.Id);
        var snapshot = (await reader.CaptureAsync(owner, CancellationToken.None)).Snapshot!;
        Assert.All(snapshot.Accounts, x => Assert.Equal(owner, x.UserId)); Assert.All(snapshot.Operations, x => Assert.Equal(owner, x.UserId));
        Assert.Equal(InspectionStatus.Forbidden, (await inspection.InspectAsync(new(Guid.NewGuid(), owner, false))).Status);
    }

    [SqlServerFact]
    public async Task BoundedCapture_AndLockFailureReturnExplicitErrorsWithoutAnyWrites()
    {
        await Open(a, 100m); var before = await OwnerRows();
        await using (var connection = await Connect())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXEC dbo.CaptureFinancialJournalInspection @owner,@MaximumRows=1;"; command.Parameters.AddWithValue("@owner", owner);
            Assert.Equal(51040, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        }
        await using var blocker = await Connect(); await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await Hold(blocker, transaction, "Accounts");
        var failed = inspection.InspectAsync(Authorized());
        try
        {
            await WaitForBlocked(1, ".reader");
            var result = await failed; Assert.Equal(InspectionStatus.ExecutionFailed, result.Status); Assert.Equal("inspection.unavailable", result.ErrorCode);
            Assert.Null(result.InspectionId); Assert.Empty(result.Findings);
        }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(before, await OwnerRows()); Assert.Equal(InspectionStatus.Healthy, (await inspection.InspectAsync(Authorized())).Status);
    }

    private async Task<string> OwnerRows()
    {
        // Hash every relevant owned row before/after; inspection must not even create an audit outcome.
        var accounts = await database.Accounts.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Id).ToListAsync();
        var batches = await database.FinancialPostingBatches.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Id).ToListAsync();
        var operations = await database.FinancialOperations.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Id).ToListAsync();
        var versions = await database.FinancialOpeningVersions.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.AccountId).ThenBy(x => x.Revision).ToListAsync();
        var outcomes = await database.FinancialRequestOutcomes.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Id).ToListAsync();
        var audits = await database.FinancialAuditEntries.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Id).ToListAsync();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accounts, batches, operations, versions, outcomes, audits }))));
    }
    private async Task<SqlConnection> Connect(string suffix = ".probe")
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName = prefix + suffix };
        var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync(); return connection;
    }
    private async Task Hold(SqlConnection connection, SqlTransaction transaction, string table)
    {
        if (table is not ("Accounts" or "FinancialAuditEntries")) throw new InvalidOperationException();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.[{table}] WITH (TABLOCKX,HOLDLOCK) WHERE UserId=@owner;";
        command.Parameters.AddWithValue("@owner", owner); await command.ExecuteScalarAsync();
    }
    private async Task<int[]> WaitForBlocked(int count, string suffix = "")
    {
        await using var observer = await Connect(); await using var command = observer.CreateCommand();
        command.CommandText = "SELECT CONVERT(int,r.session_id) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON r.session_id=s.session_id WHERE s.program_name LIKE @application AND r.wait_type LIKE N'LCK_M_%';";
        command.Parameters.AddWithValue("@application", prefix + suffix + "%"); var time = Stopwatch.StartNew();
        while (time.Elapsed < TimeSpan.FromSeconds(3))
        {
            var sessions = new List<int>(); await using (var rows = await command.ExecuteReaderAsync()) while (await rows.ReadAsync()) sessions.Add(rows.GetInt32(0));
            if (sessions.Count >= count) return sessions.ToArray(); await Task.Delay(25);
        }
        throw new InvalidOperationException("Independent SQL sessions did not reach observable lock contention.");
    }
}
