using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;
using VaultFinance.Infrastructure.Persistence.Migrations;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed class FinancialJournalConfigurationTests
{
    private const string MigrationId = "20261123215517_FinancialJournalPersistence";
    private static DateTimeOffset Instant => new(2026, 11, 24, 3, 59, 59, TimeSpan.Zero);

    [Fact]
    public void Model_PreservesDomainSeparationAndMapsExactFinancialTypes()
    {
        using var context = Context();
        Assert.Null(context.Model.FindEntityType(typeof(FinancialOperation)));
        Assert.Null(context.Model.FindEntityType(typeof(FinancialPostingContext)));
        Assert.Null(context.Model.FindEntityType(typeof(FinancialAmount)));
        Assert.Null(context.Model.FindEntityType(typeof(Money)));

        var operation = context.Model.FindEntityType(typeof(FinancialOperationRecord))!;
        Assert.Equal("FinancialOperations", operation.GetTableName());
        var amount = operation.FindProperty(nameof(FinancialOperationRecord.Amount))!;
        Assert.Equal(28, amount.GetPrecision());
        Assert.Equal(2, amount.GetScale());
        Assert.Equal("decimal(28,2)", amount.GetColumnType());
        var batch = context.Model.FindEntityType(typeof(FinancialPostingBatchRecord))!;
        Assert.Equal("datetimeoffset(7)", batch.FindProperty(nameof(FinancialPostingBatchRecord.RecordedAtUtc))!.GetColumnType());
        Assert.Equal("date", batch.FindProperty(nameof(FinancialPostingBatchRecord.EffectiveDate))!.GetColumnType());
        Assert.Equal("FinancialAccountMovements", context.Model.FindEntityType(typeof(FinancialAccountMovement))!.GetViewName());
        Assert.Null(context.Model.FindEntityType(typeof(FinancialAccountMovement))!.FindPrimaryKey());
    }

    [Fact]
    public void Model_FinancialRelationshipsNeverCascadeAndUseScopedUniqueIndexes()
    {
        using var context = Context();
        var financial = context.Model.GetEntityTypes().Where(type => type.GetTableName()?.StartsWith("Financial", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(5, financial.Length);
        Assert.All(financial.SelectMany(type => type.GetForeignKeys()), fk => Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior));
        Assert.All(financial.SelectMany(type => type.GetProperties()), property => Assert.Equal(PropertySaveBehavior.Throw, property.GetAfterSaveBehavior()));

        var outcome = context.Model.FindEntityType(typeof(FinancialRequestOutcomeRecord))!;
        Assert.Contains(outcome.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "UserId", "CommandType", "IdempotencyKey" }));
        Assert.Equal("Latin1_General_100_BIN2",
            context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(FinancialRequestOutcomeRecord))!
                .FindProperty(nameof(FinancialRequestOutcomeRecord.IdempotencyKey))!.GetCollation());
        var operation = context.Model.FindEntityType(typeof(FinancialOperationRecord))!;
        Assert.Contains(operation.GetIndexes(), index => index.IsUnique && index.GetFilter() == "[OriginalOperationId] IS NOT NULL");
    }

    [Fact]
    public void Migration_IsAdditiveAndSnapshotMatchesModel()
    {
        using var context = Context();
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = new FinancialJournalPersistence { ActiveProvider = "Microsoft.EntityFrameworkCore.SqlServer" };
        Assert.Equal(5, migration.UpOperations.OfType<CreateTableOperation>().Count());
        var accountKey = Assert.Single(migration.UpOperations.OfType<AddUniqueConstraintOperation>());
        Assert.Equal("Accounts", accountKey.Table);
        Assert.DoesNotContain(migration.UpOperations, operation =>
            operation is DropTableOperation or DropColumnOperation or AlterColumnOperation or DeleteDataOperation or UpdateDataOperation);
        string script = context.GetService<IMigrator>().GenerateScript("20261123172009_InitialPersistence", MigrationId);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE VIEW [dbo].[FinancialAccountMovements]", script);
        Assert.Equal(5, migration.UpOperations.OfType<SqlOperation>().Count(operation =>
            operation.Sql.Contains("CREATE TRIGGER", StringComparison.Ordinal)));
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("opening")]
    [InlineData("outcome")]
    [InlineData("audit")]
    public void SaveChanges_RejectsDeletingEveryFinancialRecordBeforeConnecting(string type)
    {
        using var context = Context();
        var record = Record(type);
        context.Attach(record);
        Assert.Throws<InvalidOperationException>(() =>
        {
            context.Entry(record).State = EntityState.Deleted;
            context.SaveChanges();
        });
    }

    [Theory]
    [InlineData("batch", "AccountingTimeZone")]
    [InlineData("operation", "Reason")]
    [InlineData("opening", "Reason")]
    [InlineData("outcome", "ReceiptJson")]
    [InlineData("audit", "SummaryJson")]
    public async Task SaveChanges_RejectsEditingEveryFinancialRecordBeforeConnecting(string type, string property)
    {
        using var context = Context();
        var record = Record(type);
        context.Attach(record);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            context.Entry(record).Property(property).CurrentValue = "Changed";
            await context.SaveChangesAsync();
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.001")]
    public async Task SaveChanges_RejectsInvalidAmountsBeforeSqlParameterConversion(string value)
    {
        using var context = Context();
        var record = Operation();
        context.Add(record);
        context.Entry(record).Property(nameof(FinancialOperationRecord.Amount)).CurrentValue =
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<DomainException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("VaultFinanceDb", "localhost\\SQLEXPRESS")]
    [InlineData("master", "localhost\\SQLEXPRESS")]
    [InlineData("VaultFinanceJournalTests", "remote.invalid")]
    public void SqlTestSafety_RejectsDevelopmentSystemOrRemoteTargets(string database, string server)
    {
        Assert.Throws<InvalidOperationException>(() => SqlTestDatabase.ValidateConnectionString(
            $"Server={server};Database={database};Integrated Security=True;Encrypt=True"));
    }

    [Theory]
    [InlineData("unicode-key")]
    [InlineData("long-key")]
    [InlineData("unicode-fingerprint")]
    [InlineData("long-fingerprint")]
    public async Task SaveChanges_RejectsLossyProtocolTextBeforeSqlConversion(string scenario)
    {
        using var context = Context();
        context.Add(new FinancialRequestOutcomeRecord(Guid.NewGuid(), Guid.NewGuid(), FinancialCommandType.Income,
            scenario == "unicode-key" ? "Ｋey" : scenario == "long-key" ? new string('a', 129) : "key",
            scenario == "unicode-fingerprint" ? new string('Ａ', 64) :
                new string('A', scenario == "long-fingerprint" ? 65 : 64),
            1, FinancialOutcomeKind.Rejected, null, "rejected", "{}", Instant));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public void SqlTestSafety_RequiresExplicitConnectionAndRejectsAttachments()
    {
        Assert.Throws<InvalidOperationException>(() => SqlTestDatabase.ValidateConnectionString(null));
        Assert.Throws<InvalidOperationException>(() => SqlTestDatabase.ValidateConnectionString(
            @"Server=localhost\SQLEXPRESS;Database=VaultFinanceJournalTests;Integrated Security=True;AttachDbFilename=C:\foreign.mdf"));
        using var context = SqlTestDatabase.CreateContext(
            @"Server=localhost\SQLEXPRESS;Database=VaultFinanceJournalTests;Integrated Security=True;Encrypt=True");
        Assert.Equal(SqlTestDatabase.DatabaseName, context.Database.GetDbConnection().Database);
    }

    private static VaultFinanceDbContext Context() => new(SqlTestDatabase.CreateOptions(
        "Server=configuration-only.invalid;Database=VaultFinanceJournalTests;Integrated Security=True;Encrypt=True"));

    private static FinancialOperationRecord Operation()
    {
        var owner = Guid.NewGuid();
        return FinancialOperationRecord.FromDomain(FinancialOperation.CreateIncome(Guid.NewGuid(), owner,
            new Account(Guid.NewGuid(), owner, "Synthetic", "USD"),
            new FinancialAmount(new Money(1m, "USD")), new FinancialPostingContext(Guid.NewGuid(), Instant)));
    }

    private static object Record(string type)
    {
        var owner = Guid.NewGuid();
        return type switch
        {
            "batch" => FinancialPostingBatchRecord.FromDomain(owner, FinancialCommandType.Income,
                new FinancialPostingContext(Guid.NewGuid(), Instant)),
            "operation" => Operation(),
            "opening" => new FinancialOpeningVersionRecord(owner, "USD", Guid.NewGuid(), 1, null, Guid.NewGuid(), null, "Zero"),
            "outcome" => new FinancialRequestOutcomeRecord(Guid.NewGuid(), owner, FinancialCommandType.Income, "key",
                new string('A', 64), 1, FinancialOutcomeKind.Rejected, null, "rejected", "{}", Instant),
            "audit" => new FinancialAuditEntryRecord(Guid.NewGuid(), owner, Guid.NewGuid(), Instant, "rejected", "{}"),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

}
