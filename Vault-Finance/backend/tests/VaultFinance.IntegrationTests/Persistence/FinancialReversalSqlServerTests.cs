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
public sealed partial class FinancialReversalSqlServerTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>, IAsyncLifetime
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
        scope = fixture.Provider.CreateAsyncScope();
        database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        source = new(Guid.NewGuid(), ownerId, "Synthetic reversal source", "USD");
        destination = new(Guid.NewGuid(), ownerId, "Synthetic reversal destination", "USD");
        database.Users.Add(User(ownerId)); database.AddRange(source, destination); await database.SaveChangesAsync();
        applicationName = "VaultFinance.ReversalTests." + runId;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString()) { ApplicationName = applicationName, Pooling = false };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        serviceContext = SqlTestDatabase.CreateContext(connectionString);
        service = new FinancialAccountService(new SqlFinancialJournal(serviceContext));
    }

    public async Task DisposeAsync()
    {
        if (database is null) return;
        await serviceContext.DisposeAsync(); await scope.DisposeAsync();
        // All synthetic committed history remains in the marked test database.
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income, "DOP")]
    [InlineData(FinancialOperationKind.Income, "USD")]
    [InlineData(FinancialOperationKind.Expense, "DOP")]
    [InlineData(FinancialOperationKind.Expense, "USD")]
    [InlineData(FinancialOperationKind.Transfer, "DOP")]
    [InlineData(FinancialOperationKind.Transfer, "USD")]
    public async Task Reversal_UsesOnlyOriginalFactsAndPreservesTheCompleteOriginalHistory(FinancialOperationKind kind, string currency)
    {
        if (currency != "USD")
        {
            source = new(Guid.NewGuid(), ownerId, "Synthetic DOP reversal source", currency);
            destination = new(Guid.NewGuid(), ownerId, "Synthetic DOP reversal destination", currency);
            database.AddRange(source, destination); await database.SaveChangesAsync();
        }
        Guid original = await PrepareAsync(kind);
        string originalBefore = await OriginalSnapshotAsync(original);
        var before = await CountsAsync();
        decimal totalBefore = await IndependentTotalAsync();
        var result = await ReverseAsync(original);
        Assert.Null(result.Failure);
        var receipt = Assert.IsType<FinancialReversalReceipt>(result.Receipt);
        Assert.Equal((original, kind, FinancialOperationKind.Reversal, 20m, currency),
            (receipt.OriginalOperationId, receipt.OriginalOperationKind, receipt.Kind, receipt.Amount, receipt.Currency));
        Assert.Equal(kind == FinancialOperationKind.Expense ? null : kind == FinancialOperationKind.Transfer ? destination.Id : (Guid?)source.Id, receipt.DebitAccountId);
        Assert.Equal(kind == FinancialOperationKind.Income ? null : (Guid?)source.Id, receipt.CreditAccountId);
        Assert.Equal(TimeSpan.Zero, receipt.RecordedAtUtc.Offset);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.RecordedAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime), receipt.EffectiveDate);
        var row = await database.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == receipt.OperationId);
        Assert.Equal(original, row.OriginalOperationId); Assert.Equal(kind, row.OriginalOperationKind);
        Assert.Null(row.Amount); Assert.Null(row.SourceAccountId); Assert.Null(row.DestinationAccountId);
        Assert.Equal("Synthetic reversal", row.Reason);
        var originalRow = await database.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == original);
        var originalBatch = await database.FinancialPostingBatches.AsNoTracking().SingleAsync(x => x.Id == originalRow.BatchId);
        Assert.NotEqual(originalRow.BatchId, receipt.BatchId); Assert.NotEqual(original, receipt.OperationId);
        Assert.True(receipt.RecordedAtUtc >= originalBatch.RecordedAtUtc);
        Assert.Equal(originalBefore, await OriginalSnapshotAsync(original));
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(100m, (await service.GetBalanceAsync(Owner(), source.Id)).Balance!.Amount);
        var effects = await database.FinancialAccountMovements.AsNoTracking().Where(x => x.OperationId == receipt.OperationId).ToListAsync();
        var originalEffects = await database.FinancialAccountMovements.AsNoTracking().Where(x => x.OperationId == original).ToListAsync();
        Assert.Equal(kind == FinancialOperationKind.Transfer ? 2 : 1, effects.Count);
        foreach (var effect in effects)
            Assert.Equal(-Assert.Single(originalEffects, e => e.AccountId == effect.AccountId).SignedAmount, effect.SignedAmount);
        if (kind == FinancialOperationKind.Transfer) Assert.Equal(totalBefore, await IndependentTotalAsync());
        Assert.Equal(before with { Batches = before.Batches + 1, Operations = before.Operations + 1,
            Outcomes = before.Outcomes + 1, Audits = before.Audits + 1, Effects = before.Effects + effects.Count }, await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income, "0.01")]
    [InlineData(FinancialOperationKind.Expense, "0.01")]
    [InlineData(FinancialOperationKind.Transfer, "0.01")]
    [InlineData(FinancialOperationKind.Income, "99999999999999999999999999.99")]
    [InlineData(FinancialOperationKind.Expense, "99999999999999999999999999.99")]
    [InlineData(FinancialOperationKind.Transfer, "99999999999999999999999999.99")]
    public async Task Reversal_PreservesExactMinimumAndMaximumOriginalMagnitudes(FinancialOperationKind kind, string text)
    {
        decimal amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        decimal initial = kind == FinancialOperationKind.Income ? 0m : amount;
        var original = await PrepareAsync(kind, initial, amount: amount);
        var result = await ReverseAsync(original);
        Assert.Null(result.Failure); Assert.Equal(amount, result.Receipt!.Amount);
        Assert.Equal(initial, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income, "source")]
    [InlineData(FinancialOperationKind.Expense, "source")]
    [InlineData(FinancialOperationKind.Transfer, "source")]
    [InlineData(FinancialOperationKind.Transfer, "destination")]
    [InlineData(FinancialOperationKind.Transfer, "both")]
    public async Task Reversal_AllowsOnlyExactCompensationOnArchivedAccountsWithoutReactivatingThem(FinancialOperationKind kind, string archive)
    {
        var original = await PrepareAsync(kind);
        if (archive != "destination") source.Archive();
        if (archive != "source") destination.Archive();
        await database.SaveChangesAsync();
        Assert.NotNull((await ReverseAsync(original)).Receipt);
        Assert.Equal(100m, await IndependentBalanceAsync(source.Id)); Assert.Equal(0m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(source.Status, (await database.Accounts.AsNoTracking().SingleAsync(x => x.Id == source.Id)).Status);
        Assert.Equal(destination.Status, (await database.Accounts.AsNoTracking().SingleAsync(x => x.Id == destination.Id)).Status);
        var archived = archive == "destination" ? destination : source;
        Assert.Equal(FinancialFailure.Archived, (await service.RecordIncomeAsync(Owner(), new(archived.Id, 1m, archived.Currency), "ordinary")).Failure);
    }

    [SqlServerTheory]
    [InlineData("income-funds", FinancialFailure.InsufficientFunds)]
    [InlineData("expense-limit", FinancialFailure.BalanceLimitExceeded)]
    [InlineData("transfer-funds", FinancialFailure.InsufficientFunds)]
    [InlineData("transfer-limit", FinancialFailure.BalanceLimitExceeded)]
    public async Task Rejection_IsDurableAndANewKeyCanReevaluateAfterFundsOrHeadroomChanges(string scenario, FinancialFailure expected)
    {
        bool limit = scenario.EndsWith("limit", StringComparison.Ordinal);
        var kind = scenario.StartsWith("income", StringComparison.Ordinal) ? FinancialOperationKind.Income
            : scenario.StartsWith("expense", StringComparison.Ordinal) ? FinancialOperationKind.Expense : FinancialOperationKind.Transfer;
        var original = await PrepareAsync(kind, limit ? FinancialAmount.MaximumValue : kind == FinancialOperationKind.Transfer ? 100m : 0m);
        Guid changedAccount = scenario == "transfer-funds" ? destination.Id : source.Id;
        Assert.NotNull((await EntryAsync(limit, changedAccount, 20m, "remove-capacity")).Receipt);
        var before = await CountsAsync();
        var rejected = await ReverseAsync(original);
        Assert.Equal(expected, rejected.Failure);
        Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, await CountsAsync());
        Assert.NotNull((await EntryAsync(!limit, changedAccount, 20m, "restore-capacity")).Receipt);
        Assert.Equal(rejected, await ReverseAsync(original));
        Assert.NotNull((await ReverseAsync(original, "new-intent")).Receipt);
        Assert.Equal(1, await ReversalCountAsync(original));
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_MissingAndForeignReturnTheSameNotFoundWithoutFinancialEffects(bool foreign)
    {
        Guid id = Guid.NewGuid();
        if (foreign)
        {
            var other = Guid.NewGuid(); var account = new Account(Guid.NewGuid(), other, "Synthetic foreign reversal", "USD");
            database.Users.Add(User(other)); database.Add(account); await database.SaveChangesAsync();
            Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(account.Id, 0m, "USD", "Synthetic"), "opening")).Receipt);
            id = (await service.RecordIncomeAsync(new(other), new(account.Id, 20m, "USD"), "income")).Receipt!.OperationId;
        }
        Assert.Equal(FinancialFailure.NotFound, (await ReverseAsync(id)).Failure);
        Assert.Equal(new Counts(Outcomes: 1, Audits: 1), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reversal_RejectsOpeningAndReversalWithoutChangingOpeningRevisions(bool targetReversal)
    {
        Guid id;
        if (targetReversal) id = (await ReverseAsync(await PrepareAsync(FinancialOperationKind.Income))).Receipt!.OperationId;
        else id = (await service.InitializeOpeningAsync(Owner(), new(source.Id, 100m, "USD", "Synthetic"), "opening")).Receipt!.OperationId!.Value;
        var before = await CountsAsync();
        Assert.Equal(FinancialFailure.OperationNotReversible, (await ReverseAsync(id, "forbidden")).Failure);
        Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public async Task Replay_IsStableAfterReversalAndLaterStateChangesButNewKeyIsAlreadyReversed(FinancialOperationKind kind)
    {
        var original = await PrepareAsync(kind);
        var first = await ReverseAsync(original); Assert.NotNull(first.Receipt);
        await EntryAsync(false, source.Id, 1m, "later"); source.Archive(); destination.Archive(); await database.SaveChangesAsync();
        var before = await CountsAsync();
        Assert.Equal(first, await ReverseAsync(original, reason: "  Synthetic reversal  "));
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(FinancialFailure.AlreadyReversed, (await ReverseAsync(original, "different-key")).Failure);
        Assert.Equal(1, await ReversalCountAsync(original));
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameKey_WithDifferentOriginalOrReasonConflicts(bool changeReason)
    {
        var original = await PrepareAsync(FinancialOperationKind.Income);
        var other = (await EntryAsync(true, source.Id, 5m, "second-original")).Receipt!.OperationId;
        var first = await ReverseAsync(original); var before = await CountsAsync();
        Assert.Equal(FinancialFailure.IdempotencyConflict, (await ReverseAsync(changeReason ? original : other,
            reason: changeReason ? "Different reason" : "Synthetic reversal")).Failure);
        Assert.Equal(before, await CountsAsync()); Assert.Equal(first, await ReverseAsync(original));
    }

    private FinancialOwner Owner() => new(ownerId);
    private Task<FinancialReversalOutcome> ReverseAsync(Guid original, string key = "reverse", string reason = "Synthetic reversal") =>
        service.RecordReversalAsync(Owner(), new(original, reason), key);
    private Task<FinancialEntryOutcome> EntryAsync(bool income, Guid account, decimal amount, string key) => income
        ? service.RecordIncomeAsync(Owner(), new(account, amount, source.Currency), key)
        : service.RecordExpenseAsync(Owner(), new(account, amount, source.Currency), key);
    private async Task<Guid> PrepareAsync(FinancialOperationKind kind, decimal initialSource = 100m, decimal initialDestination = 0m, decimal amount = 20m)
    {
        await OpenAsync(source, initialSource); await OpenAsync(destination, initialDestination);
        if (kind == FinancialOperationKind.Transfer)
            return (await service.RecordTransferAsync(Owner(), new(source.Id, destination.Id, amount, source.Currency), "original")).Receipt!.OperationId;
        return (await EntryAsync(kind == FinancialOperationKind.Income, source.Id, amount, "original")).Receipt!.OperationId;
    }
    private async Task OpenAsync(Account account, decimal amount) => Assert.NotNull((await service.InitializeOpeningAsync(Owner(),
        new(account.Id, amount, account.Currency, "Synthetic opening"), "open-" + account.Id.ToString("N"))).Receipt);
    private sealed record Counts(int Batches = 0, int Operations = 0, int Versions = 0, int Outcomes = 0, int Audits = 0, int Effects = 0);
    private async Task<Counts> CountsAsync() => new(
        await database.FinancialPostingBatches.CountAsync(x => x.UserId == ownerId),
        await database.FinancialOperations.CountAsync(x => x.UserId == ownerId),
        await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId),
        await database.FinancialRequestOutcomes.CountAsync(x => x.UserId == ownerId),
        await database.FinancialAuditEntries.CountAsync(x => x.UserId == ownerId),
        await database.FinancialAccountMovements.CountAsync(x => x.UserId == ownerId));
    private Task<int> ReversalCountAsync(Guid id) => database.FinancialOperations.CountAsync(x => x.UserId == ownerId && x.OriginalOperationId == id);
    private async Task<string> OriginalSnapshotAsync(Guid id)
    {
        var operation = await database.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == id);
        var batch = await database.FinancialPostingBatches.AsNoTracking().SingleAsync(x => x.Id == operation.BatchId);
        var outcome = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(x => x.BatchId == batch.Id && x.UserId == ownerId);
        var audit = await database.FinancialAuditEntries.AsNoTracking().SingleAsync(x => x.OutcomeId == outcome.Id && x.UserId == ownerId);
        return JsonSerializer.Serialize(new { operation, batch, outcome, audit });
    }
    private async Task<SqlConnection> ConnectAsync(string? name = null)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName = name ?? "VaultFinance.ReversalProbe." + runId };
        var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync(); return connection;
    }
    private async Task<decimal> IndependentBalanceAsync(Guid account)
    {
        await using var connection = await ConnectAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(SignedAmount),0) FROM dbo.FinancialAccountMovements WHERE UserId=@owner AND AccountId=@account;";
        command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@account", account);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }
    private async Task<decimal> IndependentTotalAsync() => await IndependentBalanceAsync(source.Id) + await IndependentBalanceAsync(destination.Id);
    private static ApplicationUser User(Guid id) => new() { Id = id, UserName = $"reversal-{id:N}@example.invalid", Email = $"reversal-{id:N}@example.invalid" };
}
