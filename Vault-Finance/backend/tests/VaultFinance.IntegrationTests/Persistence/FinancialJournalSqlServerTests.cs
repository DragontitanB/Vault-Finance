using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed class FinancialJournalSqlServerTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly Guid owner = Guid.NewGuid();
    private AsyncServiceScope scope;
    private VaultFinanceDbContext context = null!;
    private IDbContextTransaction? transaction;
    private Account source = null!;
    private Account destination = null!;
    private static DateTimeOffset Instant => new DateTimeOffset(2026, 11, 24, 3, 59, 59, TimeSpan.Zero).AddTicks(1234567);

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1") { return; }
        scope = fixture.Provider.CreateAsyncScope();
        context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        transaction = await context.Database.BeginTransactionAsync();
        source = new Account(Guid.NewGuid(), owner, "Synthetic source", "USD");
        destination = new Account(Guid.NewGuid(), owner, "Synthetic destination", "USD");
        context.Users.Add(User(owner));
        context.Accounts.AddRange(source, destination);
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (context is null) { return; }
        if (transaction is not null) { await transaction.DisposeAsync(); }
        await scope.DisposeAsync();
    }

    [SqlServerFact]
    public async Task Migration_IsAppliedOnlyToTheMarkedDatabaseAndVisibleFromAnIndependentConnection()
    {
        Assert.Equal(SqlTestDatabase.DatabaseName, context.Database.GetDbConnection().Database);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(new[] { "20261123172009_InitialPersistence", "20261123215517_FinancialJournalPersistence" },
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        await using var connection = new SqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.triggers
            WHERE name IN ('TR_FinancialPostingBatches_AppendOnly','TR_FinancialOperations_AppendOnly',
                'TR_FinancialOpeningVersions_AppendOnly','TR_FinancialRequestOutcomes_AppendOnly',
                'TR_FinancialAuditEntries_AppendOnly');
            """;
        Assert.Equal(5, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.FinancialOperations')
                AND name = N'Amount' AND precision = 28 AND scale = 2;
            """;
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Opening, "DOP")]
    [InlineData(FinancialOperationKind.Income, "USD")]
    [InlineData(FinancialOperationKind.Expense, "DOP")]
    [InlineData(FinancialOperationKind.Transfer, "USD")]
    public async Task Operations_RoundTripMaximumAmountKindAndParticipants(FinancialOperationKind kind, string currency)
    {
        if (currency == "DOP")
        {
            source = new Account(Guid.NewGuid(), owner, "Synthetic DOP source", currency);
            destination = new Account(Guid.NewGuid(), owner, "Synthetic DOP destination", currency);
            context.Accounts.AddRange(source, destination);
            await context.SaveChangesAsync();
        }

        var operation = Operation(kind, FinancialAmount.MaximumValue);
        await AddOperationAsync(operation);
        context.ChangeTracker.Clear();

        var row = await context.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id);
        var batch = await context.FinancialPostingBatches.AsNoTracking().SingleAsync(x => x.Id == operation.Posting.BatchId);
        Assert.Equal(operation.Kind, row.Kind);
        Assert.Equal(operation.SourceAccountId, row.SourceAccountId);
        Assert.Equal(operation.DestinationAccountId, row.DestinationAccountId);
        Assert.Equal(new FinancialAmount(new Money(FinancialAmount.MaximumValue, currency)), row.ReadAmount());
        Assert.Equal(Instant, batch.RecordedAtUtc);
        Assert.Equal(new DateOnly(2026, 11, 23), batch.EffectiveDate);
        Assert.Equal(FinancialPostingContext.AccountingTimeZoneId, batch.AccountingTimeZone);
        Assert.True(batch.Sequence > 0);
    }

    [SqlServerTheory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public async Task View_DerivesExactEffectsAndFullReversalWithoutMutatingOriginal(FinancialOperationKind kind)
    {
        var original = Operation(kind, 1.2300m);
        await AddOperationAsync(original);
        source.Archive();
        await context.SaveChangesAsync();
        var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(Instant.AddMinutes(1)), "Synthetic correction");
        await AddOperationAsync(reversal);

        var effects = await context.FinancialAccountMovements.AsNoTracking()
            .Where(x => x.UserId == owner).ToListAsync();
        var originals = effects.Where(x => x.OperationId == original.Id).ToArray();
        var inverses = effects.Where(x => x.OperationId == reversal.Id).ToArray();
        Assert.Equal(kind == FinancialOperationKind.Transfer ? 2 : 1, originals.Length);
        Assert.Equal(originals.Length, inverses.Length);
        foreach (var effect in originals)
        {
            var inverse = Assert.Single(inverses, row => row.AccountId == effect.AccountId);
            Assert.Equal(-effect.SignedAmount, inverse.SignedAmount);
            Assert.Equal(1.23m, Math.Abs(effect.SignedAmount));
        }
        var retained = await context.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == original.Id);
        var compensation = await context.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == reversal.Id);
        Assert.Equal(kind, retained.Kind);
        Assert.Equal(1.23m, retained.Amount);
        Assert.Null(compensation.Amount);
        Assert.Null(compensation.SourceAccountId);
        Assert.Null(compensation.DestinationAccountId);
        Assert.Equal(original.Id, compensation.OriginalOperationId);
    }

    [SqlServerTheory]
    [InlineData(1, "0", false, true)]
    [InlineData(2, "-1", false, true)]
    [InlineData(2, null, false, true)]
    [InlineData(2, "1", true, true)]
    [InlineData(2, "1", false, false)]
    [InlineData(3, "1", true, true)]
    [InlineData(4, "1", true, false)]
    [InlineData(5, "1", false, true)]
    [InlineData(99, "1", false, true)]
    public async Task Checks_RejectInvalidKindsMagnitudesAndParticipantShapes(int kind, string? amount, bool hasSource, bool hasDestination)
    {
        var batch = await AddBatchAsync();
        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertRawAsync(
            batch, kind, amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture),
            hasSource ? source.Id : null, hasDestination ? destination.Id : null));
        Assert.Equal(547, exception.Number);
    }

    [SqlServerFact]
    public async Task SqlDecimal_CannotDistinguishExcessScaleAfterColumnConversion()
    {
        var batch = await AddBatchAsync();
        var id = Guid.NewGuid();
        await InsertRawAsync(batch, 2, 1.001m, null, destination.Id, id: id);
        Assert.Equal(1.00m, (await context.FinancialOperations.AsNoTracking().SingleAsync(x => x.Id == id)).Amount);
        // Documents a SQL limit, not acceptance at the future financial command boundary.
        // Money/FinancialAmount and SaveChanges must reject meaningful excess scale before binding.
    }

    [SqlServerTheory]
    [InlineData("foreign-owner")]
    [InlineData("currency")]
    [InlineData("missing-batch")]
    [InlineData("empty-id")]
    [InlineData("same-account")]
    public async Task Constraints_RejectForeignOwnershipCurrencyMissingReferencesAndInvalidIds(string scenario)
    {
        var batch = await AddBatchAsync();
        var targetAccount = destination.Id;
        if (scenario == "foreign-owner")
        {
            var foreignOwner = Guid.NewGuid();
            context.Users.Add(User(foreignOwner));
            var foreign = new Account(Guid.NewGuid(), foreignOwner, "Foreign synthetic", "USD");
            context.Accounts.Add(foreign);
            await context.SaveChangesAsync();
            targetAccount = foreign.Id;
        }

        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertRawAsync(
            scenario == "missing-batch" ? Guid.NewGuid() : batch,
            scenario == "same-account" ? 4 : 2, 1m,
            scenario == "same-account" ? targetAccount : null, targetAccount,
            scenario == "currency" ? "DOP" : "USD", scenario == "empty-id" ? Guid.Empty : null));
        Assert.Equal(547, exception.Number);
    }

    [SqlServerTheory]
    [InlineData("offset")]
    [InlineData("default")]
    [InlineData("zone")]
    public async Task Constraints_RejectNonUtcDefaultInstantsAndUnknownAccountingZones(string scenario)
    {
        var batch = FinancialPostingBatchRecord.FromDomain(owner, FinancialCommandType.Income, Posting());
        context.Add(batch);
        if (scenario == "zone")
            context.Entry(batch).Property(nameof(FinancialPostingBatchRecord.AccountingTimeZone)).CurrentValue = "UTC";
        else
            context.Entry(batch).Property(nameof(FinancialPostingBatchRecord.RecordedAtUtc)).CurrentValue =
                scenario == "default" ? default(DateTimeOffset) : Instant.ToOffset(TimeSpan.FromHours(-4));
        await AssertSqlFailureAsync(() => context.SaveChangesAsync(), 547);
    }

    [SqlServerFact]
    public async Task UniqueIndex_PreventsSecondFullReversalAcrossBatches()
    {
        var original = Operation(FinancialOperationKind.Income);
        await AddOperationAsync(original);
        await AddOperationAsync(FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(), "First"));
        var second = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(), "Second");
        await AssertSqlFailureAsync(() => AddOperationAsync(second), 2601, 2627);
    }

    [SqlServerTheory]
    [InlineData("wrong-kind")]
    [InlineData("reversal-of-reversal")]
    public async Task ReversalReference_RequiresAnExistingOrdinaryOriginalOfTheDeclaredKind(string scenario)
    {
        var original = Operation(FinancialOperationKind.Income);
        await AddOperationAsync(original);
        Guid originalId = original.Id;
        int originalKind = (int)FinancialOperationKind.Expense;
        if (scenario == "reversal-of-reversal")
        {
            var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(), "Synthetic reversal");
            await AddOperationAsync(reversal);
            originalId = reversal.Id;
            originalKind = (int)FinancialOperationKind.Reversal;
        }
        var batch = await AddBatchAsync(FinancialCommandType.Reverse);
        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertRawAsync(
            batch, 5, null, null, null, originalId: originalId, originalKind: originalKind));
        Assert.Equal(547, exception.Number);
    }

    [SqlServerFact]
    public async Task OpeningVersions_SupportPositiveAndExplicitZeroHistoryWithAnAdjacentPredecessor()
    {
        var opening = Operation(FinancialOperationKind.Opening);
        await AddOperationAsync(opening);
        context.Add(new FinancialOpeningVersionRecord(owner, "USD", source.Id, 1, null, opening.Posting.BatchId, opening.Id, "Initial synthetic"));
        await context.SaveChangesAsync();
        var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, opening, Posting(), "Replace with zero");
        await AddOperationAsync(reversal);
        context.Add(new FinancialOpeningVersionRecord(owner, "USD", source.Id, 2, 1, reversal.Posting.BatchId, null, "Explicit zero"));
        await context.SaveChangesAsync();

        var versions = await context.FinancialOpeningVersions.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(opening.Id, versions[0].OpeningOperationId);
        Assert.Null(versions[1].OpeningOperationId);
        Assert.Equal(1, versions[1].PreviousRevision);
    }

    [SqlServerTheory]
    [InlineData("missing-predecessor")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-batch")]
    [InlineData("duplicate-root")]
    public async Task OpeningVersions_RejectInvalidChainsAndReferences(string scenario)
    {
        var opening = Operation(scenario == "wrong-kind" ? FinancialOperationKind.Income : FinancialOperationKind.Opening);
        await AddOperationAsync(opening);
        if (scenario == "duplicate-root")
        {
            context.Add(new FinancialOpeningVersionRecord(owner, "USD", source.Id, 1, null, opening.Posting.BatchId, null, "Zero root"));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
        var batch = scenario == "wrong-batch" ? await AddBatchAsync() : opening.Posting.BatchId;
        context.Add(new FinancialOpeningVersionRecord(owner, "USD", source.Id,
            scenario == "missing-predecessor" ? 2 : 1, scenario == "missing-predecessor" ? 1 : null,
            batch, scenario == "duplicate-root" ? null : opening.Id, "Synthetic"));
        await AssertSqlFailureAsync(() => context.SaveChangesAsync(), 547, 2601, 2627);
    }

    [SqlServerFact]
    public async Task IdempotencyIndexes_ScopeByOwnerCommandAndCaseSensitiveKey()
    {
        var other = Guid.NewGuid();
        context.Users.Add(User(other));
        context.AddRange(Outcome(owner, FinancialCommandType.Income, "Key"),
            Outcome(owner, FinancialCommandType.Expense, "Key"),
            Outcome(other, FinancialCommandType.Income, "Key"),
            Outcome(owner, FinancialCommandType.Income, "key"));
        await context.SaveChangesAsync();
        Assert.Equal(3, await context.FinancialRequestOutcomes.CountAsync(x => x.UserId == owner));

        context.Add(Outcome(owner, FinancialCommandType.Income, "Key", fingerprint: new string('B', 64)));
        await AssertSqlFailureAsync(() => context.SaveChangesAsync(), 2601, 2627);
        // No payload comparison/replay/result-return protocol is implemented by this index.
    }

    [SqlServerTheory]
    [InlineData("key")]
    [InlineData("fingerprint")]
    [InlineData("version")]
    [InlineData("json")]
    [InlineData("posted-without-batch")]
    public async Task Outcomes_RejectInvalidKeysFingerprintsVersionsReceiptsAndResultShapes(string scenario)
    {
        var id = Guid.NewGuid();
        var key = scenario == "key" ? "has space" : "key";
        var fingerprint = scenario == "fingerprint" ? new string('G', 64) : new string('A', 64);
        int version = scenario == "version" ? 0 : 1;
        int outcome = scenario == "posted-without-batch" ? 1 : 2;
        var receipt = scenario == "json" ? "not-json" : "{}";
        var exception = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [dbo].[FinancialRequestOutcomes]
                ([Id],[UserId],[CommandType],[IdempotencyKey],[PayloadFingerprint],[FingerprintVersion],
                 [OutcomeKind],[BatchId],[ResultCode],[ReceiptJson],[RecordedAtUtc])
            VALUES ({id},{owner},{3},{key},{fingerprint},{version},{outcome},NULL,
                    {"synthetic.rejected"},{receipt},{Instant});
            """));
        Assert.Equal(547, exception.Number);
    }

    [SqlServerFact]
    public async Task Audit_RejectsForeignOwnerAndPreservesOutcomeOwnership()
    {
        var other = Guid.NewGuid();
        context.Users.Add(User(other));
        var outcome = Outcome(owner, FinancialCommandType.Income, "audit");
        context.Add(outcome);
        await context.SaveChangesAsync();
        context.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), other, outcome.Id, Instant, "synthetic.rejected", "{}"));
        await AssertSqlFailureAsync(() => context.SaveChangesAsync(), 547);
    }

    [SqlServerFact]
    public async Task History_RejectsSqlUpdatesDeletesAndCascadesAcrossIndependentConnections()
    {
        var opening = Operation(FinancialOperationKind.Opening);
        await AddOperationAsync(opening);
        context.Add(new FinancialOpeningVersionRecord(owner, "USD", source.Id, 1, null, opening.Posting.BatchId, opening.Id, "Synthetic retained opening"));
        var outcome = new FinancialRequestOutcomeRecord(Guid.NewGuid(), owner, FinancialCommandType.InitializeOpening,
            Guid.NewGuid().ToString("N"), new string('A', 64), 1, FinancialOutcomeKind.Posted,
            opening.Posting.BatchId, "synthetic.posted", "{}", Instant);
        context.Add(outcome);
        context.Add(new FinancialAuditEntryRecord(Guid.NewGuid(), owner, outcome.Id, Instant, "synthetic.posted", "{}"));
        await context.SaveChangesAsync();
        await transaction!.CommitAsync();
        await transaction.DisposeAsync();
        transaction = null;

        // Deliberately retain this synthetic immutable history in the marked test database.
        await using var connection = new SqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@owner", owner);
        foreach (string table in new[] { "FinancialPostingBatches", "FinancialOperations", "FinancialOpeningVersions", "FinancialRequestOutcomes", "FinancialAuditEntries" })
        {
            foreach (string verb in new[] { "UPDATE", "DELETE" })
            {
                command.CommandText = verb == "UPDATE"
                    ? $"UPDATE [dbo].[{table}] SET [UserId] = [UserId] WHERE [UserId] = @owner;"
                    : $"DELETE FROM [dbo].[{table}] WHERE [UserId] = @owner;";
                var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(51001, exception.Number);
            }
            command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}] WHERE [UserId] = @owner;";
            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        command.CommandText = "DELETE FROM [dbo].[Accounts] WHERE [UserId] = @owner;";
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        command.CommandText = "DELETE FROM [dbo].[AspNetUsers] WHERE [Id] = @owner;";
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }

    private async Task<Guid> AddBatchAsync(FinancialCommandType command = FinancialCommandType.Income)
    {
        var posting = Posting();
        context.Add(FinancialPostingBatchRecord.FromDomain(owner, command, posting));
        await context.SaveChangesAsync();
        return posting.BatchId;
    }

    private async Task AddOperationAsync(FinancialOperation operation)
    {
        var command = operation.Kind switch
        {
            FinancialOperationKind.Opening => FinancialCommandType.InitializeOpening,
            FinancialOperationKind.Expense => FinancialCommandType.Expense,
            FinancialOperationKind.Transfer => FinancialCommandType.Transfer,
            FinancialOperationKind.Reversal => FinancialCommandType.Reverse,
            _ => FinancialCommandType.Income
        };
        context.Add(FinancialPostingBatchRecord.FromDomain(owner, command, operation.Posting));
        context.Add(FinancialOperationRecord.FromDomain(operation));
        await context.SaveChangesAsync();
    }

    private Task<int> InsertRawAsync(Guid batch, int kind, decimal? amount, Guid? sourceId, Guid? destinationId,
        string currency = "USD", Guid? id = null, Guid? originalId = null, int? originalKind = null)
    {
        var operationId = id ?? Guid.NewGuid();
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [dbo].[FinancialOperations]
                ([Id],[UserId],[BatchId],[Kind],[Currency],[Amount],[SourceAccountId],[DestinationAccountId],
                 [OriginalOperationId],[OriginalOperationKind],[Reason])
            VALUES ({operationId},{owner},{batch},{kind},{currency},{amount},{sourceId},{destinationId},
                    {originalId},{originalKind},{"Synthetic fixture"});
            """);
    }

    private FinancialOperation Operation(FinancialOperationKind kind, decimal value = 10m)
    {
        var amount = new FinancialAmount(new Money(value, source.Currency));
        var id = Guid.NewGuid();
        return kind switch
        {
            FinancialOperationKind.Opening => FinancialOperation.CreateOpening(id, owner, source, amount, Posting(), "Synthetic opening"),
            FinancialOperationKind.Income => FinancialOperation.CreateIncome(id, owner, source, amount, Posting()),
            FinancialOperationKind.Expense => FinancialOperation.CreateExpense(id, owner, source, amount, Posting()),
            FinancialOperationKind.Transfer => FinancialOperation.CreateTransfer(id, owner, source, destination, amount, Posting()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static FinancialPostingContext Posting(DateTimeOffset? instant = null) => new(Guid.NewGuid(), instant ?? Instant);
    private static ApplicationUser User(Guid id) => new() { Id = id, Email = $"sql-{id:N}@example.invalid", UserName = $"sql-{id:N}@example.invalid" };
    private static FinancialRequestOutcomeRecord Outcome(Guid ownerId, FinancialCommandType command, string key, string? fingerprint = null) =>
        new(Guid.NewGuid(), ownerId, command, key, fingerprint ?? new string('A', 64), 1, FinancialOutcomeKind.Rejected,
            null, "synthetic.rejected", "{}", Instant);

    private static async Task AssertSqlFailureAsync(Func<Task> action, params int[] numbers)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        Assert.Contains(Assert.IsType<SqlException>(exception.InnerException).Number, numbers);
    }
}
