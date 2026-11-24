using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.IntegrationTests.Persistence;

[Collection(FinancialSqlProtocolCollection.Name)]
public sealed partial class OpeningCorrectionSqlServerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly Guid ownerId = Guid.NewGuid();
    private readonly string runId = Guid.NewGuid().ToString("N");
    private AsyncServiceScope scope;
    private VaultFinanceDbContext database = null!;
    private VaultFinanceDbContext serviceContext = null!;
    private IFinancialAccountService service = null!;
    private Account source = null!;
    private Account destination = null!;
    private string connectionString = null!;
    private string applicationName = null!;
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") return;
        scope = fixture.Provider.CreateAsyncScope(); database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        source = new(Guid.NewGuid(), ownerId, "Synthetic correction source", "USD");
        destination = new(Guid.NewGuid(), ownerId, "Synthetic correction destination", "USD");
        database.Users.Add(User(ownerId)); database.AddRange(source, destination); await database.SaveChangesAsync();
        applicationName = "VaultFinance.CorrectionTests." + runId;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString()) { ApplicationName = applicationName, Pooling = false };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        serviceContext = SqlTestDatabase.CreateContext(connectionString); service = new FinancialAccountService(new SqlFinancialJournal(serviceContext));
    }
    public async Task DisposeAsync()
    {
        if (database is null) return;
        await serviceContext.DisposeAsync(); await scope.DisposeAsync();
        // Retain all synthetic committed financial histories; no purge/reset.
    }

    [SqlServerTheory]
    [InlineData("100", "80", "DOP")]
    [InlineData("100", "120", "USD")]
    [InlineData("0", "20", "DOP")]
    [InlineData("0", "0.01", "USD")]
    [InlineData("100", "0", "DOP")]
    [InlineData("100", "0", "USD")]
    [InlineData("0", "99999999999999999999999999.99", "USD")]
    [InlineData("99999999999999999999999999.99", "0", "USD")]
    public async Task Correction_AppendsOnlyTheRequiredComponentsAndPreservesHistory(string oldText, string newText, string currency)
    {
        if (currency != "USD") { source = new(Guid.NewGuid(), ownerId, "Synthetic DOP correction", currency); database.Add(source); await database.SaveChangesAsync(); }
        decimal oldAmount = Number(oldText), amount = Number(newText);
        var root = await OpenAsync(source, oldAmount); var history = await CaptureHistoryAsync(); var before = await CountsAsync();
        var result = await CorrectAsync(amount, currency: " " + currency.ToLowerInvariant() + " ", reason: "  Synthetic correction  ");
        Assert.Null(result.Failure); var receipt = Assert.IsType<OpeningCorrectionReceipt>(result.Receipt);
        Assert.Equal((source.Id, 1, 2, oldAmount, amount, currency),
            (receipt.AccountId, receipt.PreviousRevision, receipt.Revision, receipt.PreviousOpeningAmount, receipt.OpeningAmount, receipt.Currency));
        Assert.Equal(root.OperationId, receipt.PreviousOpeningOperationId);
        Assert.Equal(oldAmount > 0m, receipt.CompensationOperationId.HasValue); Assert.Equal(amount > 0m, receipt.OpeningOperationId.HasValue);
        Assert.True(receipt.RecordedAtUtc >= root.RecordedAtUtc); Assert.Equal(TimeSpan.Zero, receipt.RecordedAtUtc.Offset);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.RecordedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime), receipt.EffectiveDate);
        await AssertPreservedAsync(history);
        await AssertCorrectionBatchAsync(receipt);
        Assert.Equal(amount, await IndependentBalanceAsync(source.Id));
        Assert.Equal(amount, (await service.GetBalanceAsync(Owner(), source.Id)).Balance!.Amount);
        int operations = (oldAmount > 0m ? 1 : 0) + (amount > 0m ? 1 : 0);
        Assert.Equal(before with { Batches = before.Batches + 1, Operations = before.Operations + operations,
            Versions = before.Versions + 1, Outcomes = before.Outcomes + 1, Audits = before.Audits + 1, Effects = before.Effects + operations }, await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("1.23")]
    public async Task SameAmount_IsAStableRejectedNoChangeWithoutANewVersion(string text)
    {
        decimal amount = Number(text); await OpenAsync(source, amount); var before = await CountsAsync();
        var result = await CorrectAsync(amount); Assert.Equal(FinancialFailure.OpeningUnchanged, result.Failure);
        Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, await CountsAsync());
        Assert.Equal(result, await CorrectAsync(amount));
        Assert.NotNull((await CorrectAsync(amount + 1m, "changed")).Receipt);
        Assert.Equal(result, await CorrectAsync(amount));
    }

    [SqlServerFact]
    public async Task SuccessiveCorrections_FormOneContiguousChainAndInternalComponentsCannotBeFreelyReversed()
    {
        await OpenAsync(source, 100m);
        int revision = 1;
        foreach (decimal amount in new[] { 80m, 0m, 20m, 60m, 0m })
        {
            var before = await CaptureHistoryAsync();
            var result = await CorrectAsync(amount, "revision-" + revision, revision);
            Assert.NotNull(result.Receipt); await AssertCorrectionBatchAsync(result.Receipt); await AssertPreservedAsync(before);
            revision++;
            Assert.Equal(amount, await IndependentBalanceAsync(source.Id));
            foreach (var id in new[] { result.Receipt.OpeningOperationId, result.Receipt.CompensationOperationId }.OfType<Guid>())
                Assert.Equal(FinancialFailure.OperationNotReversible, (await service.RecordReversalAsync(Owner(), new(id, "Forbidden ordinary reversal"), "forbidden-" + id.ToString("N"))).Failure);
        }
        var versions = await database.FinancialOpeningVersions.AsNoTracking().Where(x => x.UserId == ownerId && x.AccountId == source.Id).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 6), versions.Select(x => x.Revision));
        Assert.Null(versions[0].PreviousRevision);
        Assert.All(versions.Skip(1), x => Assert.Equal(x.Revision - 1, x.PreviousRevision));
        Assert.All(versions.GroupBy(x => x.PreviousRevision), group => Assert.Single(group));
    }

    [SqlServerTheory]
    [InlineData("80", null, "0")]
    [InlineData("70", FinancialFailure.InsufficientFunds, "20")]
    public async Task Correction_ValidatesTheFinalBatchInsteadOfTheNegativeCompensationPrefix(string desired, FinancialFailure? failure, string expected)
    {
        await OpenAsync(source, 100m); Assert.NotNull((await EntryAsync(false, 80m, "expense")).Receipt);
        var before = await CountsAsync(); var result = await CorrectAsync(Number(desired)); Assert.Equal(failure, result.Failure);
        if (failure is null) await AssertCorrectionBatchAsync(result.Receipt!);
        else Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, await CountsAsync());
        Assert.Equal(Number(expected), await IndependentBalanceAsync(source.Id));
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FundsAndOverflowRejections_RemainStableUntilANewKeyIsUsed(bool overflow)
    {
        await OpenAsync(source, overflow ? 0m : 100m);
        if (overflow) await EntryAsync(true, FinancialAmount.MaximumValue, "fill"); else await EntryAsync(false, 80m, "spend");
        decimal amount = overflow ? 1m : 70m;
        var rejected = await CorrectAsync(amount);
        Assert.Equal(overflow ? FinancialFailure.BalanceLimitExceeded : FinancialFailure.InsufficientFunds, rejected.Failure);
        await EntryAsync(!overflow, 10m, "capacity"); Assert.Equal(rejected, await CorrectAsync(amount));
        Assert.NotNull((await CorrectAsync(amount, "new-key")).Receipt);
    }

    [SqlServerFact]
    public async Task LaterIncomeExpenseTransferAndReversalRemainUnchangedByCorrection()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var income = (await EntryAsync(true, 50m, "income")).Receipt!;
        await EntryAsync(false, 20m, "expense");
        Assert.NotNull((await service.RecordTransferAsync(Owner(), new(source.Id, destination.Id, 30m, "USD"), "transfer")).Receipt);
        Assert.NotNull((await service.RecordReversalAsync(Owner(), new(income.OperationId, "Synthetic reversal"), "reverse")).Receipt);
        var history = await CaptureHistoryAsync(); Assert.Equal(50m, await IndependentBalanceAsync(source.Id));
        var result = await CorrectAsync(80m); Assert.NotNull(result.Receipt); await AssertCorrectionBatchAsync(result.Receipt);
        await AssertPreservedAsync(history); Assert.Equal(30m, await IndependentBalanceAsync(source.Id)); Assert.Equal(30m, await IndependentBalanceAsync(destination.Id));
    }

    [SqlServerTheory]
    [InlineData("missing", FinancialFailure.NotFound)]
    [InlineData("foreign", FinancialFailure.NotFound)]
    [InlineData("archived", FinancialFailure.Archived)]
    [InlineData("uninitialized", FinancialFailure.Uninitialized)]
    [InlineData("currency", FinancialFailure.CurrencyMismatch)]
    public async Task InvalidAccountState_IsOwnerScopedAndCreatesOnlyARejectedOutcome(string scenario, FinancialFailure expected)
    {
        Guid id = source.Id;
        if (scenario == "missing") id = Guid.NewGuid();
        else if (scenario == "foreign")
        {
            var owner = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), owner, "Synthetic foreign correction", "USD");
            database.Users.Add(User(owner)); database.Add(account); await database.SaveChangesAsync(); id = account.Id;
        }
        else if (scenario != "uninitialized") { await OpenAsync(source, 100m); if (scenario == "archived") { source.Archive(); await database.SaveChangesAsync(); } }
        var before = await CountsAsync(); Assert.Equal(expected, (await CorrectAsync(80m, id: id, currency: scenario == "currency" ? "DOP" : "USD")).Failure);
        Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData("100", "0", null)]
    [InlineData("0", "0", FinancialFailure.OpeningUnchanged)]
    [InlineData("100", "80", FinancialFailure.Archived)]
    [InlineData("0", "20", FinancialFailure.Archived)]
    public async Task ArchivedPolicy_AllowsOnlyExactCancellationToZero(string oldAmount, string amount, FinancialFailure? failure)
    {
        await OpenAsync(source, Number(oldAmount)); source.Archive(); await database.SaveChangesAsync();
        var result = await CorrectAsync(Number(amount)); Assert.Equal(failure, result.Failure);
        if (failure is null) { Assert.NotNull(result.Receipt); await AssertCorrectionBatchAsync(result.Receipt); Assert.Equal(0m, await IndependentBalanceAsync(source.Id)); }
        Assert.Equal(AccountStatus.Archived, (await database.Accounts.AsNoTracking().SingleAsync(x => x.Id == source.Id)).Status);
    }

    [SqlServerTheory]
    [InlineData("-1", "USD", 1)]
    [InlineData("1.001", "USD", 1)]
    [InlineData("100000000000000000000000000", "USD", 1)]
    [InlineData("1", "EUR", 1)]
    [InlineData("1", "USD", 0)]
    public async Task MalformedInput_DoesNotReserveAKey(string amount, string currency, int revision)
    {
        Assert.Equal(FinancialFailure.InvalidInput, (await CorrectAsync(Number(amount), revision: revision, currency: currency)).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task ObsoleteOrFutureRevision_IsAStableVersionConflict(int expected)
    {
        await OpenAsync(source, 100m); await CorrectAsync(80m, "first");
        var result = await CorrectAsync(60m, revision: expected); Assert.Equal(FinancialFailure.OpeningVersionConflict, result.Failure);
        Assert.Equal(result, await CorrectAsync(60m, revision: expected));
        Assert.NotNull((await CorrectAsync(60m, "fresh", 2)).Receipt);
    }

    [SqlServerFact]
    public async Task Replay_ReturnsHistoricalReceiptAfterLaterVersionsAndArchive()
    {
        await OpenAsync(source, 100m); var first = await CorrectAsync(80m); await CorrectAsync(60m, "next", 2);
        source.Archive(); await database.SaveChangesAsync(); var before = await CountsAsync();
        Assert.Equal(first, await CorrectAsync(80.00m, currency: " usd ", reason: "  Synthetic correction  "));
        Assert.Equal(before, await CountsAsync()); Assert.Equal(60m, await IndependentBalanceAsync(source.Id));
    }

    [SqlServerTheory]
    [InlineData("amount")]
    [InlineData("revision")]
    [InlineData("account")]
    [InlineData("currency")]
    [InlineData("reason")]
    public async Task SameKey_ConflictsOnChangedValidPayload(string change)
    {
        await OpenAsync(source, 100m); Assert.NotNull((await CorrectAsync(80m)).Receipt); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.IdempotencyConflict, (await CorrectAsync(change == "amount" ? 60m : 80m,
            revision: change == "revision" ? 2 : 1, id: change == "account" ? destination.Id : source.Id,
            currency: change == "currency" ? "DOP" : "USD", reason: change == "reason" ? "Different" : "Synthetic correction")).Failure);
        Assert.Equal(before, await CountsAsync());
    }

    private FinancialOwner Owner() => new(ownerId);
    private Task<OpeningCorrectionOutcome> CorrectAsync(decimal amount, string key = "correction", int revision = 1, Guid? id = null, string currency = "USD", string reason = "Synthetic correction") =>
        service.CorrectOpeningAsync(Owner(), new(id ?? source.Id, amount, currency, revision, reason), key);
    private Task<FinancialEntryOutcome> EntryAsync(bool income, decimal amount, string key) => income
        ? service.RecordIncomeAsync(Owner(), new(source.Id, amount, source.Currency), key) : service.RecordExpenseAsync(Owner(), new(source.Id, amount, source.Currency), key);
    private async Task<OpeningReceipt> OpenAsync(Account account, decimal amount)
    {
        var result = await service.InitializeOpeningAsync(Owner(), new(account.Id, amount, account.Currency, "Synthetic opening"), "open-" + account.Id.ToString("N"));
        Assert.Null(result.Failure); return Assert.IsType<OpeningReceipt>(result.Receipt);
    }
    private sealed record Counts(int Batches = 0, int Operations = 0, int Versions = 0, int Outcomes = 0, int Audits = 0, int Effects = 0);
    private async Task<Counts> CountsAsync() => new(await database.FinancialPostingBatches.CountAsync(x => x.UserId == ownerId),
        await database.FinancialOperations.CountAsync(x => x.UserId == ownerId), await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId),
        await database.FinancialRequestOutcomes.CountAsync(x => x.UserId == ownerId), await database.FinancialAuditEntries.CountAsync(x => x.UserId == ownerId),
        await database.FinancialAccountMovements.CountAsync(x => x.UserId == ownerId));
    private async Task AssertCorrectionBatchAsync(OpeningCorrectionReceipt receipt)
    {
        var batch = await database.FinancialPostingBatches.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.Id == receipt.BatchId);
        Assert.Equal(FinancialCommandType.CorrectOpening, batch.CommandType); Assert.Equal(receipt.RecordedAtUtc, batch.RecordedAtUtc);
        var rows = await database.FinancialOperations.AsNoTracking().Where(x => x.UserId == ownerId && x.BatchId == receipt.BatchId).ToListAsync();
        Assert.Equal((receipt.CompensationOperationId.HasValue ? 1 : 0) + (receipt.OpeningOperationId.HasValue ? 1 : 0), rows.Count);
        if (receipt.CompensationOperationId is Guid comp)
        {
            var row = Assert.Single(rows, x => x.Id == comp); Assert.Equal(FinancialOperationKind.Reversal, row.Kind);
            Assert.Equal(receipt.PreviousOpeningOperationId, row.OriginalOperationId); Assert.Equal(FinancialOperationKind.Opening, row.OriginalOperationKind);
            Assert.Null(row.Amount); Assert.Null(row.SourceAccountId); Assert.Null(row.DestinationAccountId);
        }
        if (receipt.OpeningOperationId is Guid replacement)
        {
            var row = Assert.Single(rows, x => x.Id == replacement); Assert.Equal(FinancialOperationKind.Opening, row.Kind);
            Assert.Equal(receipt.OpeningAmount, row.Amount); Assert.Equal(receipt.AccountId, row.DestinationAccountId); Assert.Null(row.OriginalOperationId);
        }
        var version = await database.FinancialOpeningVersions.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.AccountId == receipt.AccountId && x.Revision == receipt.Revision);
        Assert.Equal(receipt.PreviousRevision, version.PreviousRevision); Assert.Equal(receipt.OpeningOperationId, version.OpeningOperationId); Assert.Equal(receipt.BatchId, version.BatchId);
        Assert.Equal(receipt.OpeningAmount - receipt.PreviousOpeningAmount,
            await database.FinancialAccountMovements.Where(x => x.UserId == ownerId && x.BatchId == receipt.BatchId).SumAsync(x => x.SignedAmount));
        var outcome = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.BatchId == receipt.BatchId);
        Assert.Equal(FinancialCommandType.CorrectOpening, outcome.CommandType);
        Assert.Equal("financial.opening_correction.posted", (await database.FinancialAuditEntries.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.OutcomeId == outcome.Id)).Action);
    }
    private async Task<Dictionary<string, string>> CaptureHistoryAsync()
    {
        var result = new Dictionary<string, string>();
        foreach (var row in await database.FinancialOperations.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync()) result.Add("op-" + row.Id, JsonSerializer.Serialize(row));
        foreach (var row in await database.FinancialPostingBatches.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync()) result.Add("batch-" + row.Id, JsonSerializer.Serialize(row));
        foreach (var row in await database.FinancialOpeningVersions.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync()) result.Add("version-" + row.AccountId + "-" + row.Revision, JsonSerializer.Serialize(row));
        foreach (var row in await database.FinancialRequestOutcomes.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync()) result.Add("outcome-" + row.Id, JsonSerializer.Serialize(row));
        foreach (var row in await database.FinancialAuditEntries.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync()) result.Add("audit-" + row.Id, JsonSerializer.Serialize(row));
        return result;
    }
    private async Task AssertPreservedAsync(Dictionary<string, string> before)
    { var after = await CaptureHistoryAsync(); foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]); }
    private async Task<SqlConnection> ConnectAsync(string? name = null)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName = name ?? "VaultFinance.CorrectionProbe." + runId };
        var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync(); return connection;
    }
    private async Task<decimal> IndependentBalanceAsync(Guid account)
    {
        await using var connection = await ConnectAsync(); await using var command = connection.CreateCommand();
        // Independent reconstruction from canonical rows/references, not the production effects view.
        command.CommandText = """
            SELECT COALESCE(SUM(e.Delta),0) FROM dbo.FinancialOperations o
            LEFT JOIN dbo.FinancialOperations p ON p.UserId=o.UserId AND p.Id=o.OriginalOperationId
            CROSS APPLY (VALUES
                (CASE WHEN o.Kind=5 THEN p.DestinationAccountId ELSE o.DestinationAccountId END,
                 CONVERT(decimal(28,2),CASE WHEN o.Kind=5 THEN -p.Amount ELSE o.Amount END)),
                (CASE WHEN o.Kind=5 THEN p.SourceAccountId ELSE o.SourceAccountId END,
                 CONVERT(decimal(28,2),CASE WHEN o.Kind=5 THEN p.Amount ELSE -o.Amount END))) e(AccountId,Delta)
            WHERE o.UserId=@owner AND e.AccountId=@account;
            """;
        command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@account", account);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }
    private static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
    private static ApplicationUser User(Guid id) => new() { Id = id, UserName = $"correction-{id:N}@example.invalid", Email = $"correction-{id:N}@example.invalid" };
}
