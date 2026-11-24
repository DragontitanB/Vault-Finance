using System.Globalization;
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
public sealed partial class FinancialTransferSqlServerTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>, IAsyncLifetime
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
        source = new(Guid.NewGuid(), ownerId, "Synthetic transfer source", "USD");
        destination = new(Guid.NewGuid(), ownerId, "Synthetic transfer destination", "USD");
        database.Users.Add(User(ownerId)); database.AddRange(source, destination);
        await database.SaveChangesAsync();
        applicationName = "VaultFinance.TransferTests." + runId;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString())
        { ApplicationName = applicationName, Pooling = false };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        serviceContext = SqlTestDatabase.CreateContext(connectionString);
        service = new FinancialAccountService(new SqlFinancialJournal(serviceContext));
    }

    public async Task DisposeAsync()
    {
        if (database is null) return;
        await serviceContext.DisposeAsync();
        await scope.DisposeAsync();
        // Retain synthetic committed histories; never purge financial rows.
    }

    [SqlServerTheory]
    [InlineData("DOP", "100", "20", "35.23")]
    [InlineData("USD", "100", "0", "100")]
    [InlineData("USD", "99999999999999999999999999.99", "0", "99999999999999999999999999.99")]
    [InlineData("DOP", "99999999999999999999999999.99", "99999999999999999999999998.99", "1")]
    public async Task Transfer_ConservesExactBalancesWithOneCanonicalOperationAndTwoEffects(string currency, string initialSource, string initialDestination, string text)
    {
        if (currency != "USD")
        {
            source = await AddAccountAsync(currency);
            destination = await AddAccountAsync(currency);
        }
        decimal from = Number(initialSource), to = Number(initialDestination), amount = Number(text);
        await OpenAsync(source, from); await OpenAsync(destination, to);
        var result = await TransferAsync(amount, currency: " " + currency.ToLowerInvariant() + " ", reason: "  Synthetic transfer  ");
        Assert.Null(result.Failure);
        var receipt = Assert.IsType<FinancialTransferReceipt>(result.Receipt);
        Assert.Equal((source.Id, destination.Id, amount, currency, FinancialOperationKind.Transfer),
            (receipt.SourceAccountId, receipt.DestinationAccountId, receipt.Amount, receipt.Currency, receipt.Kind));
        Assert.Equal(TimeSpan.Zero, receipt.RecordedAtUtc.Offset);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.RecordedAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime), receipt.EffectiveDate);
        var row = await database.FinancialOperations.AsNoTracking().SingleAsync(x => x.BatchId == receipt.BatchId);
        Assert.Equal(receipt.OperationId, row.Id);
        Assert.Equal((source.Id, destination.Id), (row.SourceAccountId, row.DestinationAccountId));
        Assert.Equal(FinancialOperationKind.Transfer, row.Kind);
        Assert.Equal(amount, row.Amount);
        Assert.Equal("Synthetic transfer", row.Reason);
        Assert.Null(row.OriginalOperationId);
        var effects = await database.FinancialAccountMovements.AsNoTracking().Where(x => x.BatchId == receipt.BatchId).ToListAsync();
        Assert.Equal(2, effects.Count);
        Assert.Equal(-amount, Assert.Single(effects, x => x.AccountId == source.Id).SignedAmount);
        Assert.Equal(amount, Assert.Single(effects, x => x.AccountId == destination.Id).SignedAmount);
        Assert.Equal(0m, effects.Sum(x => x.SignedAmount));
        Assert.Equal(from - amount, await IndependentBalanceAsync(source.Id));
        Assert.Equal(to + amount, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(from + to, await IndependentTotalAsync(source.Id, destination.Id));
        Assert.Equal(from - amount, (await service.GetBalanceAsync(Owner(), source.Id)).Balance!.Amount);
        Assert.Equal(to + amount, (await service.GetBalanceAsync(Owner(), destination.Id)).Balance!.Amount);
        Assert.Equal(1, await TransferOperationCountAsync());
    }

    [SqlServerTheory]
    [InlineData(true, "missing", FinancialFailure.NotFound)]
    [InlineData(false, "missing", FinancialFailure.NotFound)]
    [InlineData(true, "foreign", FinancialFailure.NotFound)]
    [InlineData(false, "foreign", FinancialFailure.NotFound)]
    [InlineData(true, "archived", FinancialFailure.Archived)]
    [InlineData(false, "archived", FinancialFailure.Archived)]
    [InlineData(true, "uninitialized", FinancialFailure.Uninitialized)]
    [InlineData(false, "uninitialized", FinancialFailure.Uninitialized)]
    [InlineData(true, "currency", FinancialFailure.CurrencyMismatch)]
    [InlineData(false, "currency", FinancialFailure.CurrencyMismatch)]
    public async Task Transfer_RejectsInvalidParticipantsInEitherPosition(bool invalidSource, string scenario, FinancialFailure failure)
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 100m);
        var invalid = invalidSource ? source : destination;
        Guid id = invalid.Id;
        switch (scenario)
        {
            case "missing": id = Guid.NewGuid(); break;
            case "foreign":
                var other = Guid.NewGuid(); database.Users.Add(User(other));
                invalid = new(Guid.NewGuid(), other, "Synthetic foreign account", "USD");
                database.Add(invalid); await database.SaveChangesAsync(); id = invalid.Id; break;
            case "archived": invalid.Archive(); await database.SaveChangesAsync(); break;
            case "uninitialized": invalid = await AddAccountAsync(); id = invalid.Id; break;
            case "currency": invalid = await AddAccountAsync("DOP"); await OpenAsync(invalid, 100m); id = invalid.Id; break;
        }
        var before = await CountsAsync();
        var result = await TransferAsync(1m, from: invalidSource ? id : source.Id, to: invalidSource ? destination.Id : id);
        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Receipt);
        var after = await CountsAsync();
        Assert.Equal(before with { Outcomes = before.Outcomes + 1, Audits = before.Audits + 1 }, after);
        Assert.Equal(0, await TransferOperationCountAsync());
        Assert.Equal(200m, await IndependentTotalAsync(source.Id, destination.Id));
    }

    [SqlServerTheory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("100000000000000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public async Task Transfer_InvalidAmountsDoNotReserveKeys(string value)
    {
        Assert.Equal(FinancialFailure.InvalidInput, (await TransferAsync(Number(value))).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData("same")]
    [InlineData("empty-source")]
    [InlineData("empty-destination")]
    [InlineData("currency")]
    public async Task Transfer_InvalidShapeDoesNotReserveKeys(string scenario)
    {
        Assert.Equal(FinancialFailure.InvalidInput, (await TransferAsync(1m,
            from: scenario == "empty-source" ? Guid.Empty : source.Id,
            to: scenario == "same" ? source.Id : scenario == "empty-destination" ? Guid.Empty : destination.Id,
            currency: scenario == "currency" ? "EUR" : "USD")).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData("same")]
    [InlineData("zero")]
    [InlineData("precision")]
    [InlineData("range")]
    [InlineData("currency")]
    public async Task SqlBoundary_ValidatesAgainInsteadOfTrustingTheIntentRecord(string scenario)
    {
        var journal = new SqlFinancialJournal(serviceContext);
        var intent = new ValidatedFinancialTransfer(source.Id, scenario == "same" ? source.Id : destination.Id,
            scenario == "zero" ? 0m : scenario == "precision" ? 1.001m : scenario == "range" ? decimal.MaxValue : 1m,
            scenario == "currency" ? "EUR" : "USD", null, "key");
        Assert.Equal(FinancialFailure.InvalidInput, (await journal.RecordTransferAsync(Owner(), intent, CancellationToken.None)).Failure);
        Assert.Equal(new Counts(), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_IsPersistedAndRequiresANewKeyToReevaluate(bool destinationOverflow)
    {
        await OpenAsync(source, destinationOverflow ? 1m : 0m);
        await OpenAsync(destination, destinationOverflow ? FinancialAmount.MaximumValue : 0m);
        var rejected = await TransferAsync(1m);
        Assert.Equal(destinationOverflow ? FinancialFailure.BalanceLimitExceeded : FinancialFailure.InsufficientFunds, rejected.Failure);
        Assert.NotNull((destinationOverflow
            ? await service.RecordExpenseAsync(Owner(), new(destination.Id, 1m, "USD"), "headroom")
            : await service.RecordIncomeAsync(Owner(), new(source.Id, 1m, "USD"), "funds")).Receipt);
        Assert.Equal(rejected, await TransferAsync(1m));
        Assert.NotNull((await TransferAsync(1m, "new-intent")).Receipt);
        Assert.Equal(0m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(destinationOverflow ? FinancialAmount.MaximumValue : 1m, await IndependentBalanceAsync(destination.Id));
        Assert.Equal(1, await TransferOperationCountAsync());
    }

    [SqlServerFact]
    public async Task Replay_ReturnsTheOriginalReceiptAfterBalanceAndArchiveChanges()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        var first = await TransferAsync(10.00m, reason: "  Synthetic transfer  ");
        Assert.NotNull(first.Receipt);
        await service.RecordIncomeAsync(Owner(), new(destination.Id, 5m, "USD"), "later");
        source.Archive(); destination.Archive(); await database.SaveChangesAsync();
        var before = await CountsAsync();
        Assert.Equal(first, await TransferAsync(10m, currency: " usd ", reason: "Synthetic transfer"));
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(90m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(15m, await IndependentBalanceAsync(destination.Id));
    }

    [SqlServerTheory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("direction")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("reason")]
    public async Task Replay_ConflictsOnAnyChangedIntentIncludingDirection(string changed)
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 100m);
        Assert.NotNull((await TransferAsync(1m)).Receipt);
        var before = await CountsAsync();
        Assert.Equal(FinancialFailure.IdempotencyConflict, (await TransferAsync(changed == "amount" ? 2m : 1m,
            from: changed == "source" ? Guid.NewGuid() : changed == "direction" ? destination.Id : source.Id,
            to: changed == "destination" ? Guid.NewGuid() : changed == "direction" ? source.Id : destination.Id,
            currency: changed == "currency" ? "DOP" : "USD", reason: changed == "reason" ? "Changed" : null)).Failure);
        Assert.Equal(before, await CountsAsync());
        Assert.Equal(1, await TransferOperationCountAsync());
    }

    [SqlServerFact]
    public async Task IdempotencyScope_SeparatesTransferFromOtherCommandsAndOwners()
    {
        await OpenAsync(source, 100m); await OpenAsync(destination, 0m);
        Assert.NotNull((await service.RecordIncomeAsync(Owner(), new(source.Id, 1m, "USD"), "transfer")).Receipt);
        Assert.NotNull((await service.RecordExpenseAsync(Owner(), new(source.Id, 1m, "USD"), "transfer")).Receipt);
        Assert.NotNull((await TransferAsync(1m)).Receipt);
        var other = Guid.NewGuid(); database.Users.Add(User(other));
        var a = new Account(Guid.NewGuid(), other, "Synthetic other source", "USD");
        var b = new Account(Guid.NewGuid(), other, "Synthetic other destination", "USD");
        database.AddRange(a, b); await database.SaveChangesAsync();
        Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(a.Id, 2m, "USD", "Synthetic"), "open-a")).Receipt);
        Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(b.Id, 0m, "USD", "Synthetic"), "open-b")).Receipt);
        Assert.NotNull((await service.RecordTransferAsync(new(other), new(a.Id, b.Id, 1m, "USD"), "transfer")).Receipt);
        Assert.Equal(99m, await IndependentBalanceAsync(source.Id));
        Assert.Equal(1, await TransferOperationCountAsync());
    }

    private FinancialOwner Owner() => new(ownerId);
    private Task<FinancialTransferOutcome> TransferAsync(decimal amount, string key = "transfer", Guid? from = null, Guid? to = null,
        string currency = "USD", string? reason = null) =>
        service.RecordTransferAsync(Owner(), new(from ?? source.Id, to ?? destination.Id, amount, currency, reason), key);
    private async Task OpenAsync(Account account, decimal amount)
    {
        Assert.NotNull((await service.InitializeOpeningAsync(Owner(), new(account.Id, amount, account.Currency, "Synthetic opening"), "open-" + account.Id.ToString("N"))).Receipt);
    }
    private async Task<Account> AddAccountAsync(string currency = "USD")
    {
        var account = new Account(Guid.NewGuid(), ownerId, "Synthetic transfer participant", currency);
        database.Add(account); await database.SaveChangesAsync(); return account;
    }
    private sealed record Counts(int Batches = 0, int Operations = 0, int Versions = 0, int Outcomes = 0, int Audits = 0, int Effects = 0);
    private async Task<Counts> CountsAsync() => new(
        await database.FinancialPostingBatches.CountAsync(x => x.UserId == ownerId),
        await database.FinancialOperations.CountAsync(x => x.UserId == ownerId),
        await database.FinancialOpeningVersions.CountAsync(x => x.UserId == ownerId),
        await database.FinancialRequestOutcomes.CountAsync(x => x.UserId == ownerId),
        await database.FinancialAuditEntries.CountAsync(x => x.UserId == ownerId),
        await database.FinancialAccountMovements.CountAsync(x => x.UserId == ownerId));
    private Task<int> TransferOperationCountAsync() => database.FinancialOperations.CountAsync(x => x.UserId == ownerId && x.Kind == FinancialOperationKind.Transfer);
    private async Task<SqlConnection> ConnectAsync(string? name = null)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName = name ?? "VaultFinance.TransferProbe." + runId };
        var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync(); return connection;
    }
    private async Task<decimal> IndependentBalanceAsync(Guid id) => await IndependentTotalAsync(id, id);
    private async Task<decimal> IndependentTotalAsync(Guid a, Guid b)
    {
        await using var connection = await ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(SignedAmount),0) FROM dbo.FinancialAccountMovements WHERE UserId=@owner AND AccountId IN (@a,@b);";
        command.Parameters.AddWithValue("@owner", ownerId); command.Parameters.AddWithValue("@a", a); command.Parameters.AddWithValue("@b", b);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }
    private static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
    private static ApplicationUser User(Guid id) => new() { Id = id, UserName = $"transfer-{id:N}@example.invalid", Email = $"transfer-{id:N}@example.invalid" };
}
