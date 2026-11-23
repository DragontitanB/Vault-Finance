using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

public sealed partial class FinancialOpeningSqlServerTests(SqlServerFixture fixture)
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
        account = new Account(Guid.NewGuid(), ownerId, "Synthetic opening account", "USD");
        secondAccount = new Account(Guid.NewGuid(), ownerId, "Synthetic second account", "USD");
        database.Users.Add(User(ownerId));
        database.Accounts.AddRange(account, secondAccount);
        // Committed synthetic setup is necessary: the writer and race probes use independent connections.
        await database.SaveChangesAsync();
        applicationName = "VaultFinance.OpeningTests." + runId;
        var target = new SqlConnectionStringBuilder(database.Database.GetConnectionString())
        {
            ApplicationName = applicationName, Pooling = false
        };
        connectionString = SqlTestDatabase.ValidateConnectionString(target.ConnectionString);
        serviceContext = SqlTestDatabase.CreateContext(connectionString);
        service = new FinancialAccountService(new SqlFinancialJournal(serviceContext));
    }

    public async Task DisposeAsync()
    {
        if (database is null) { return; }
        await serviceContext.DisposeAsync();
        await scope.DisposeAsync();
        // Retain synthetic committed history; never delete financial records or reset the database.
    }

    [SqlServerTheory]
    [InlineData("0", "DOP")]
    [InlineData("0.01", "USD")]
    [InlineData("1.2300", "DOP")]
    [InlineData("99999999999999999999999999.99", "USD")]
    public async Task Opening_PostsZeroOrPositiveIntentAndReturnsAnExactDerivedBalance(string text, string currency)
    {
        if (currency == "DOP")
        {
            account = new Account(Guid.NewGuid(), ownerId, "Synthetic DOP", "DOP");
            database.Add(account);
            await database.SaveChangesAsync();
        }
        decimal amount = decimal.Parse(text, CultureInfo.InvariantCulture);
        var result = await OpenAsync(amount, currency: currency);
        Assert.Null(result.Failure);
        var receipt = Assert.IsType<OpeningReceipt>(result.Receipt);
        Assert.Equal(account.Id, receipt.AccountId);
        Assert.Equal(amount, receipt.OpeningAmount);
        Assert.Equal(1, receipt.Revision);
        Assert.Equal(TimeSpan.Zero, receipt.RecordedAtUtc.Offset);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receipt.RecordedAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime), receipt.EffectiveDate);
        Assert.Equal(amount == 0m, receipt.OperationId is null);

        var balance = await service.GetBalanceAsync(Owner(), account.Id);
        Assert.Null(balance.Failure);
        Assert.Equal(new DerivedAccountBalance(account.Id, currency, true, amount), balance.Balance);
        Assert.Equal((1, amount > 0m ? 1 : 0, 1, 1, 1, amount > 0m ? 1 : 0), await CountsAsync());
        var batch = await database.FinancialPostingBatches.AsNoTracking().SingleAsync(row => row.Id == receipt.BatchId);
        Assert.Equal(receipt.RecordedAtUtc, batch.RecordedAtUtc);
        Assert.Equal(receipt.EffectiveDate, batch.EffectiveDate);
        var audit = await database.FinancialAuditEntries.AsNoTracking().SingleAsync(row => row.UserId == ownerId);
        Assert.Equal(receipt.RecordedAtUtc, audit.RecordedAtUtc);
    }

    [SqlServerFact]
    public async Task Balance_DistinguishesUninitializedFromExplicitZeroAndAllowsArchivedHistory()
    {
        Assert.Equal(new DerivedAccountBalance(account.Id, "USD", false, null),
            (await service.GetBalanceAsync(Owner(), account.Id)).Balance);
        await OpenAsync(0m);
        account.Archive();
        await database.SaveChangesAsync();
        Assert.Equal(new DerivedAccountBalance(account.Id, "USD", true, 0m),
            (await service.GetBalanceAsync(Owner(), account.Id)).Balance);
    }

    [SqlServerTheory]
    [InlineData("foreign", FinancialFailure.NotFound)]
    [InlineData("missing", FinancialFailure.NotFound)]
    [InlineData("archived", FinancialFailure.Archived)]
    [InlineData("currency", FinancialFailure.CurrencyMismatch)]
    public async Task Opening_RejectsForeignMissingArchivedAndWrongCurrencyAccounts(string scenario, FinancialFailure expected)
    {
        Guid id = account.Id;
        if (scenario == "missing") { id = Guid.NewGuid(); }
        if (scenario == "foreign")
        {
            var foreignOwner = Guid.NewGuid();
            database.Users.Add(User(foreignOwner));
            var foreign = new Account(Guid.NewGuid(), foreignOwner, "Foreign synthetic", "USD");
            database.Add(foreign);
            await database.SaveChangesAsync();
            id = foreign.Id;
        }
        if (scenario == "archived") { account.Archive(); await database.SaveChangesAsync(); }
        var result = await OpenAsync(1m, id: id, currency: scenario == "currency" ? "DOP" : "USD");
        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Receipt);
        Assert.Equal((0, 0, 0, 1, 1, 0), await CountsAsync());
        if (scenario is "foreign" or "missing")
            Assert.Equal(FinancialFailure.NotFound, (await service.GetBalanceAsync(Owner(), id)).Failure);
    }

    [SqlServerFact]
    public async Task Opening_RejectsAnotherInitialOpeningAndPersistsTheBusinessRejection()
    {
        Assert.NotNull((await OpenAsync(10m)).Receipt);
        var rejected = await OpenAsync(20m, "different-key");
        Assert.Equal(FinancialFailure.AlreadyInitialized, rejected.Failure);
        Assert.Equal(rejected, await OpenAsync(20m, "different-key"));
        Assert.Equal((1, 1, 1, 2, 2, 1), await CountsAsync());
        Assert.Equal(10m, (await service.GetBalanceAsync(Owner(), account.Id)).Balance!.Amount);
    }

    [SqlServerFact]
    public async Task Replay_ReturnsTheSameReceiptAfterArchiveWithoutReexecuting()
    {
        var first = await OpenAsync(1.2300m, reason: "  Synthetic opening  ");
        account.Archive();
        await database.SaveChangesAsync();
        var replay = await OpenAsync(1.23m, currency: " usd ", reason: "Synthetic opening");
        Assert.Equal(first, replay);
        Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
    }

    [SqlServerFact]
    public async Task Replay_PreservesAnOriginalRejectionEvenAfterTheAccountAppears()
    {
        var missing = Guid.NewGuid();
        var rejected = await OpenAsync(1m, id: missing);
        Assert.Equal(FinancialFailure.NotFound, rejected.Failure);
        database.Add(new Account(missing, ownerId, "Now exists", "USD"));
        await database.SaveChangesAsync();
        Assert.Equal(rejected, await OpenAsync(1m, id: missing));
        Assert.NotNull((await OpenAsync(1m, "new-intent", missing)).Receipt);
    }

    [SqlServerTheory]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("reason")]
    [InlineData("account")]
    public async Task Replay_RejectsChangedPayloadWithoutMutatingTheOriginal(string changed)
    {
        var first = await OpenAsync(1m);
        var conflict = await OpenAsync(changed == "amount" ? 2m : 1m,
            id: changed == "account" ? secondAccount.Id : account.Id,
            currency: changed == "currency" ? "DOP" : "USD",
            reason: changed == "reason" ? "Changed reason" : "Synthetic opening");
        Assert.Equal(FinancialFailure.IdempotencyConflict, conflict.Failure);
        Assert.Equal(first, await OpenAsync(1m));
        Assert.Equal((1, 1, 1, 1, 1, 1), await CountsAsync());
    }

    [SqlServerFact]
    public async Task Fingerprint_IsVersionedCanonicalUtf16AndIgnoresGeneratedMetadata()
    {
        const string reason = "Synthetic \"opening\"\nα";
        var response = await OpenAsync(1.2300m, reason: " " + reason + " ");
        Assert.NotNull(response.Receipt);
        var stored = await database.FinancialRequestOutcomes.AsNoTracking().SingleAsync(row => row.UserId == ownerId);
        string canonical = $"VF-OPEN\n1\n{account.Id:D}\n1.23\nUSD\n{reason.Length}:{reason}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))), stored.PayloadFingerprint);
        Assert.Equal(1, stored.FingerprintVersion);
        Assert.Equal(response, await OpenAsync(1.23m, currency: "usd", reason: reason));
    }

    [SqlServerFact]
    public async Task IdempotencyKey_IsOwnerScopedAndCaseSensitive()
    {
        Assert.NotNull((await OpenAsync(1m, "Key")).Receipt);
        Assert.Equal(FinancialFailure.AlreadyInitialized, (await OpenAsync(1m, "key")).Failure);
        var other = Guid.NewGuid();
        var otherAccount = new Account(Guid.NewGuid(), other, "Other synthetic", "USD");
        database.Users.Add(User(other)); database.Add(otherAccount); await database.SaveChangesAsync();
        Assert.NotNull((await service.InitializeOpeningAsync(new(other),
            new(otherAccount.Id, 1m, "USD", "Synthetic opening"), "Key")).Receipt);
        Assert.Equal(2, await database.FinancialPostingBatches.CountAsync(row => row.UserId == ownerId || row.UserId == other));
    }

    [SqlServerTheory]
    [InlineData("negative")]
    [InlineData("range")]
    [InlineData("precision")]
    [InlineData("currency")]
    [InlineData("key")]
    public async Task InvalidInput_DoesNotReserveAKeyOrCreateFinancialHistory(string invalid)
    {
        decimal amount = invalid switch { "negative" => -1m, "range" => FinancialAmount.MaximumValue + 0.01m, "precision" => 1.001m, _ => 1m };
        var result = await OpenAsync(amount, invalid == "key" ? "bad key" : "key", currency: invalid == "currency" ? "EUR" : "USD");
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData("1.001")]
    [InlineData("1.2300")]
    [InlineData("-0.01")]
    [InlineData("100000000000000000000000000.00")]
    [InlineData("1e3")]
    public async Task SqlBoundary_RejectsUncanonicalOrExcessiveAmountsBeforeAnyNarrowing(string amount)
    {
        await using var connection = await ConnectAsync();
        await using var command = OpeningCommand(connection, amount, "key");
        Assert.Equal(51010, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerFact]
    public async Task Adapter_DoesNotRoundAmountsWhenApplicationValidationIsBypassed()
    {
        var journal = new SqlFinancialJournal(serviceContext);
        var result = await journal.InitializeOpeningAsync(Owner(),
            new(account.Id, 1.001m, "USD", "Synthetic opening", "key"), CancellationToken.None);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure);
        Assert.Equal((0, 0, 0, 0, 0, 0), await CountsAsync());
    }

    [SqlServerTheory]
    [InlineData(3, 59, 59, 23)]
    [InlineData(4, 0, 0, 24)]
    public async Task SqlAccountingCalendar_MatchesTheApprovedIanaZone(int hour, int minute, int second, int day)
    {
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId("America/Santo_Domingo", out var windows));
        Assert.Equal("SA Western Standard Time", windows);
        var utc = new DateTimeOffset(2026, 11, 24, hour, minute, second, TimeSpan.Zero);
        await using var connection = await ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(date, @instant AT TIME ZONE 'SA Western Standard Time');";
        command.Parameters.Add("@instant", SqlDbType.DateTimeOffset).Value = utc;
        var actual = DateOnly.FromDateTime((DateTime)(await command.ExecuteScalarAsync())!);
        Assert.Equal(new DateOnly(2026, 11, day), actual);
    }

    private FinancialOwner Owner() => new(ownerId);
    private Task<OpeningOutcome> OpenAsync(decimal amount, string key = "key", Guid? id = null,
        string currency = "USD", string reason = "Synthetic opening") =>
        service.InitializeOpeningAsync(Owner(), new(id ?? account.Id, amount, currency, reason), key);

    private async Task<(int Batches, int Operations, int Versions, int Outcomes, int Audits, int Effects)> CountsAsync() =>
        (await database.FinancialPostingBatches.CountAsync(row => row.UserId == ownerId),
         await database.FinancialOperations.CountAsync(row => row.UserId == ownerId),
         await database.FinancialOpeningVersions.CountAsync(row => row.UserId == ownerId),
         await database.FinancialRequestOutcomes.CountAsync(row => row.UserId == ownerId),
         await database.FinancialAuditEntries.CountAsync(row => row.UserId == ownerId),
         await database.FinancialAccountMovements.CountAsync(row => row.UserId == ownerId));

    private async Task<SqlConnection> ConnectAsync(string? name = null)
    {
        var target = new SqlConnectionStringBuilder(connectionString) { ApplicationName = name ?? "VaultFinance.Probe." + runId };
        var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private SqlCommand OpeningCommand(SqlConnection connection, string amount, string key)
    {
        var command = new SqlCommand("dbo.InitializeFinancialOpening", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = ownerId;
        command.Parameters.Add("@AccountId", SqlDbType.UniqueIdentifier).Value = account.Id;
        command.Parameters.Add("@AmountText", SqlDbType.NVarChar, -1).Value = amount;
        command.Parameters.Add("@Currency", SqlDbType.NVarChar, -1).Value = "USD";
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = "Synthetic opening";
        command.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, -1).Value = key;
        return command;
    }

    private static ApplicationUser User(Guid id) => new() { Id = id, UserName = $"opening-{id:N}@example.invalid", Email = $"opening-{id:N}@example.invalid" };
}
