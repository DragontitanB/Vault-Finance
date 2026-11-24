using System.Data;
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
public sealed partial class FinancialIncomeExpenseSqlServerTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly Guid ownerId = Guid.NewGuid();
    private readonly string runId = Guid.NewGuid().ToString("N");
    private AsyncServiceScope scope;
    private VaultFinanceDbContext database = null!;
    private VaultFinanceDbContext serviceContext = null!;
    private IFinancialAccountService service = null!;
    private Account account = null!;
    private Account secondAccount = null!;
    private string connectionString = null!;
    private string applicationName = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") { return; }
        scope = fixture.Provider.CreateAsyncScope();
        database = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        account = new Account(Guid.NewGuid(), ownerId, "Synthetic financial account", "USD");
        secondAccount = new Account(Guid.NewGuid(), ownerId, "Synthetic second account", "USD");
        database.Users.Add(User(ownerId)); database.Accounts.AddRange(account, secondAccount);
        await database.SaveChangesAsync();
        applicationName = "VaultFinance.EntryTests." + runId;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString())
        { ApplicationName = applicationName, Pooling = false };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        serviceContext = SqlTestDatabase.CreateContext(connectionString);
        service = new FinancialAccountService(new SqlFinancialJournal(serviceContext));
    }

    public async Task DisposeAsync()
    {
        if (database is null) { return; }
        await serviceContext.DisposeAsync();
        await scope.DisposeAsync();
        // Synthetic committed histories stay in the marked database, never purged.
    }

    [SqlServerTheory]
    [InlineData(true, "0", "1.2300", "DOP", "1.23")]
    [InlineData(false, "10000", "7000", "USD", "3000")]
    [InlineData(false, "10000", "10000", "DOP", "0")]
    [InlineData(true, "0", "99999999999999999999999999.99", "USD", "99999999999999999999999999.99")]
    [InlineData(false, "99999999999999999999999999.99", "99999999999999999999999999.99", "USD", "0")]
    public async Task Entry_PostsTheExactSignedEffectAndReturnsAnImmutableReceipt(bool income,
        string initial, string text, string currency, string expected)
    {
        if (currency != account.Currency)
        {
            account = new Account(Guid.NewGuid(), ownerId, "Synthetic DOP account", currency);
            database.Add(account); await database.SaveChangesAsync();
        }
        await OpenAsync(Number(initial));
        decimal amount = Number(text);
        var result = await PostAsync(income, amount, currency: " " + currency.ToLowerInvariant() + " ", reason: "  Synthetic entry  ");
        Assert.Null(result.Failure);
        var receipt = Assert.IsType<FinancialEntryReceipt>(result.Receipt);
        Assert.Equal(income ? FinancialOperationKind.Income : FinancialOperationKind.Expense, receipt.Kind);
        Assert.Equal(amount, receipt.Amount);
        Assert.Equal(account.Id, receipt.AccountId);
        Assert.Equal(currency, receipt.Currency);
        Assert.Equal(TimeSpan.Zero, receipt.RecordedAtUtc.Offset);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.RecordedAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime), receipt.EffectiveDate);

        var row = await database.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == receipt.OperationId);
        Assert.Equal(receipt.Kind, row.Kind);
        Assert.Equal(income ? null : (Guid?)account.Id, row.SourceAccountId);
        Assert.Equal(income ? (Guid?)account.Id : null, row.DestinationAccountId);
        Assert.Equal("Synthetic entry", row.Reason);
        Assert.Null(row.OriginalOperationId);
        Assert.Equal(Number(expected), await IndependentBalanceAsync());
        Assert.Equal(Number(expected), (await service.GetBalanceAsync(Owner(), account.Id)).Balance!.Amount);
        var outcome = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.BatchId == receipt.BatchId);
        var audit = await database.FinancialAuditEntries.AsNoTracking().SingleAsync(x => x.UserId == ownerId && x.OutcomeId == outcome.Id);
        Assert.Equal(receipt.RecordedAtUtc, audit.RecordedAtUtc);
        Assert.DoesNotContain(typeof(FinancialEntryReceipt).GetProperties(), property => property.Name.Contains("Balance", StringComparison.Ordinal));
    }

    [SqlServerTheory]
    [InlineData(true, "foreign", FinancialFailure.NotFound)]
    [InlineData(false, "foreign", FinancialFailure.NotFound)]
    [InlineData(true, "missing", FinancialFailure.NotFound)]
    [InlineData(false, "missing", FinancialFailure.NotFound)]
    [InlineData(true, "archived", FinancialFailure.Archived)]
    [InlineData(false, "archived", FinancialFailure.Archived)]
    [InlineData(true, "uninitialized", FinancialFailure.Uninitialized)]
    [InlineData(false, "uninitialized", FinancialFailure.Uninitialized)]
    [InlineData(true, "currency", FinancialFailure.CurrencyMismatch)]
    [InlineData(false, "currency", FinancialFailure.CurrencyMismatch)]
    public async Task Entry_RejectsInvalidAccountStateWithoutRevealingForeignExistence(bool income, string scenario, FinancialFailure failure)
    {
        Guid id = account.Id;
        if (scenario == "foreign")
        {
            var other = Guid.NewGuid();
            var foreign = new Account(Guid.NewGuid(), other, "Foreign synthetic", "USD");
            database.Users.Add(User(other)); database.Add(foreign); await database.SaveChangesAsync();
            id = foreign.Id;
        }
        else if (scenario == "missing") { id = Guid.NewGuid(); }
        else if (scenario == "archived") { account.Archive(); await database.SaveChangesAsync(); }
        else if (scenario == "currency") { await OpenAsync(100m); }

        var result = await PostAsync(income, 1m, id: id, currency: scenario == "currency" ? "DOP" : "USD");
        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Receipt);
        int roots = scenario == "currency" ? 1 : 0;
        Assert.Equal((roots, roots, roots, roots + 1, roots + 1, roots), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true, "0")]
    [InlineData(false, "0")]
    [InlineData(true, "-1")]
    [InlineData(false, "-0.01")]
    [InlineData(true, "1.001")]
    [InlineData(false, "1.001")]
    [InlineData(true, "100000000000000000000000000")]
    [InlineData(false, "79228162514264337593543950335")]
    public async Task Entry_RejectsInvalidAmountsWithoutReservingKeys(bool income, string amount)
    {
        var result = await PostAsync(income, Number(amount));
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entry_RejectsUnsupportedCurrencyBeforeSql(bool income)
    {
        Assert.Equal(FinancialFailure.InvalidInput, (await PostAsync(income, 1m, currency: "EUR")).Failure);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerFact]
    public async Task Expense_InsufficientFundsIsAStableRejectedResultAfterIncome()
    {
        await OpenAsync(100m);
        var rejected = await PostAsync(false, 200m, "declined");
        Assert.Equal(FinancialFailure.InsufficientFunds, rejected.Failure);
        Assert.NotNull((await PostAsync(true, 200m, "funds")).Receipt);
        Assert.Equal(rejected, await PostAsync(false, 200m, "declined"));
        Assert.NotNull((await PostAsync(false, 200m, "new-intent")).Receipt);
        Assert.Equal(100m, await IndependentBalanceAsync());
        Assert.Equal((3, 3, 1, 4, 4, 3), await CountsAsync());
    }

    [SqlServerFact]
    public async Task Income_BalanceLimitRejectionRemainsStableAfterHeadroomAppears()
    {
        await OpenAsync(FinancialAmount.MaximumValue);
        var rejected = await PostAsync(true, 0.01m, "limit");
        Assert.Equal(FinancialFailure.BalanceLimitExceeded, rejected.Failure);
        await PostAsync(false, 1m, "headroom");
        Assert.Equal(rejected, await PostAsync(true, 0.01m, "limit"));
        Assert.NotNull((await PostAsync(true, 0.01m, "new-intent")).Receipt);
        Assert.Equal(FinancialAmount.MaximumValue - 1m + 0.01m, await IndependentBalanceAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replay_ReturnsTheOriginalReceiptAfterBalanceAndArchiveChanges(bool income)
    {
        await OpenAsync(10000m);
        var first = await PostAsync(income, 1000m, reason: "  Original entry  ");
        Assert.NotNull(first.Receipt);
        await PostAsync(!income, 500m, "later");
        account.Archive(); await database.SaveChangesAsync();
        var replay = await PostAsync(income, 1000.00m, currency: " usd ", reason: "Original entry");
        Assert.Equal(first, replay);
        Assert.Equal((3, 3, 1, 3, 3, 3), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true, "amount")]
    [InlineData(false, "amount")]
    [InlineData(true, "reason")]
    [InlineData(false, "currency")]
    [InlineData(true, "account")]
    [InlineData(false, "account")]
    public async Task Replay_ConflictsOnChangedIntentWithoutAnotherEffect(bool income, string changed)
    {
        await OpenAsync(100m);
        var first = await PostAsync(income, 1m);
        var result = await PostAsync(income, changed == "amount" ? 2m : 1m,
            id: changed == "account" ? secondAccount.Id : account.Id,
            currency: changed == "currency" ? "DOP" : "USD", reason: changed == "reason" ? "Changed" : null);
        Assert.Equal(FinancialFailure.IdempotencyConflict, result.Failure);
        Assert.Equal(first, await PostAsync(income, 1m));
        Assert.Equal((2, 2, 1, 2, 2, 2), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Replay_UninitializedRejectionIsNotReevaluatedAfterOpening(bool income)
    {
        var rejected = await PostAsync(income, 1m);
        Assert.Equal(FinancialFailure.Uninitialized, rejected.Failure);
        await OpenAsync(100m);
        Assert.Equal(rejected, await PostAsync(income, 1m));
        Assert.NotNull((await PostAsync(income, 1m, "new-intent")).Receipt);
    }

    [SqlServerFact]
    public async Task IdempotencyScope_SeparatesOwnerAndCommandTypeAndNormalizesOptionalReason()
    {
        await OpenAsync(100m);
        var income = await PostAsync(true, 10m, "same", reason: " \t ");
        Assert.Equal(income, await PostAsync(true, 10.00m, "same", currency: "usd", reason: null));
        Assert.NotNull((await PostAsync(false, 10m, "same")).Receipt);
        var other = Guid.NewGuid();
        var otherAccount = new Account(Guid.NewGuid(), other, "Other owner synthetic", "USD");
        database.Users.Add(User(other)); database.Add(otherAccount); await database.SaveChangesAsync();
        Assert.NotNull((await service.InitializeOpeningAsync(new(other), new(otherAccount.Id, 100m, "USD", "Synthetic"), "opening")).Receipt);
        Assert.NotNull((await service.RecordIncomeAsync(new(other), new(otherAccount.Id, 10m, "USD"), "same")).Receipt);
        Assert.NotNull((await service.RecordExpenseAsync(new(other), new(otherAccount.Id, 10m, "USD"), "same")).Receipt);
        Assert.Equal(100m, await IndependentBalanceAsync());
    }

    [SqlServerTheory]
    [InlineData(true, "0")]
    [InlineData(false, "-1")]
    [InlineData(true, "1.001")]
    [InlineData(false, "1.001")]
    public async Task SqlBoundary_DoesNotTrustTheValidatedIntentType(bool income, string value)
    {
        var journal = new SqlFinancialJournal(serviceContext);
        var entry = new ValidatedFinancialEntry(account.Id, Number(value), "USD", null, "key");
        var result = income ? await journal.RecordIncomeAsync(Owner(), entry, CancellationToken.None)
            : await journal.RecordExpenseAsync(Owner(), entry, CancellationToken.None);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    private async Task OpenAsync(decimal amount)
    {
        var result = await service.InitializeOpeningAsync(Owner(), new(account.Id, amount, account.Currency, "Synthetic opening"), "opening");
        Assert.NotNull(result.Receipt);
    }
    private FinancialOwner Owner() => new(ownerId);
    private Task<FinancialEntryOutcome> PostAsync(bool income, decimal amount, string key="entry",
        Guid? id=null, string currency="USD", string? reason=null) =>
        income ? service.RecordIncomeAsync(Owner(), new(id ?? account.Id, amount, currency, reason), key)
            : service.RecordExpenseAsync(Owner(), new(id ?? account.Id, amount, currency, reason), key);

    private async Task<(int Batches,int Operations,int Versions,int Outcomes,int Audits,int Effects)> CountsAsync() =>
        (await database.FinancialPostingBatches.CountAsync(x=>x.UserId==ownerId),
         await database.FinancialOperations.CountAsync(x=>x.UserId==ownerId),
         await database.FinancialOpeningVersions.CountAsync(x=>x.UserId==ownerId),
         await database.FinancialRequestOutcomes.CountAsync(x=>x.UserId==ownerId),
         await database.FinancialAuditEntries.CountAsync(x=>x.UserId==ownerId),
         await database.FinancialAccountMovements.CountAsync(x=>x.UserId==ownerId));

    private async Task<SqlConnection> ConnectAsync(string? name=null)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName=name ?? "VaultFinance.EntryProbe."+runId };
        var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
    private async Task<decimal> IndependentBalanceAsync()
    {
        await using var connection=await ConnectAsync();
        await using var command=connection.CreateCommand();
        command.CommandText="SELECT COALESCE(SUM(SignedAmount),0) FROM dbo.FinancialAccountMovements WHERE UserId=@owner AND AccountId=@account;";
        command.Parameters.AddWithValue("@owner",ownerId); command.Parameters.AddWithValue("@account",account.Id);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }
    private static decimal Number(string text)=>decimal.Parse(text,CultureInfo.InvariantCulture);
    private static ApplicationUser User(Guid id)=>new() {Id=id,UserName=$"entry-{id:N}@example.invalid",Email=$"entry-{id:N}@example.invalid"};
}
