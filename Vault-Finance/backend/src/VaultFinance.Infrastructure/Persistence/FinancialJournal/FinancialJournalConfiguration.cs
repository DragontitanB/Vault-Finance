using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Infrastructure.Identity;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

internal static class FinancialJournalConfiguration
{
    private const string EmptyGuid = "'00000000-0000-0000-0000-000000000000'";
    private static readonly string TrimCharacters = string.Join(" + ", new[]
    {
        9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195,
        8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288
    }.Select(code => $"NCHAR({code})"));

    public static void Configure(ModelBuilder model)
    {
        ConfigureBatch(model.Entity<FinancialPostingBatchRecord>());
        ConfigureOperation(model.Entity<FinancialOperationRecord>());
        ConfigureOpening(model.Entity<FinancialOpeningVersionRecord>());
        ConfigureOutcome(model.Entity<FinancialRequestOutcomeRecord>());
        ConfigureAudit(model.Entity<FinancialAuditEntryRecord>());
        ConfigureMovement(model.Entity<FinancialAccountMovement>());
    }

    private static void ConfigureBatch(EntityTypeBuilder<FinancialPostingBatchRecord> b)
    {
        b.ToTable("FinancialPostingBatches", t =>
        {
            JournalTable(t, "FinancialPostingBatches");
            t.HasCheckConstraint("CK_FinancialPostingBatches_Ids", $"[Id] <> {EmptyGuid} AND [UserId] <> {EmptyGuid}");
            t.HasCheckConstraint("CK_FinancialPostingBatches_Command", "[CommandType] IN (1,2,3,4,5,6)");
            t.HasCheckConstraint("CK_FinancialPostingBatches_Utc", Utc("[RecordedAtUtc]"));
            t.HasCheckConstraint("CK_FinancialPostingBatches_Zone",
                "[AccountingTimeZone] COLLATE Latin1_General_100_BIN2 = 'America/Santo_Domingo' AND DATALENGTH([AccountingTimeZone]) = 21");
        });
        b.HasKey(x => x.Id).IsClustered(false);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.UserId, x.Id });
        b.HasAlternateKey(x => new { x.UserId, x.CommandType, x.Id });
        b.Property(x => x.Sequence).UseIdentityColumn();
        b.HasIndex(x => x.Sequence).IsUnique().IsClustered();
        b.HasIndex(x => new { x.UserId, x.EffectiveDate, x.Sequence });
        b.Property(x => x.CommandType).HasConversion<int>();
        b.Property(x => x.RecordedAtUtc).HasColumnType("datetimeoffset(7)");
        b.Property(x => x.EffectiveDate).HasColumnType("date");
        b.Property(x => x.AccountingTimeZone).HasColumnType("varchar(64)").IsRequired();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        ImmutableProperties(b);
    }

    private static void ConfigureOperation(EntityTypeBuilder<FinancialOperationRecord> b)
    {
        b.ToTable("FinancialOperations", t =>
        {
            JournalTable(t, "FinancialOperations");
            t.HasCheckConstraint("CK_FinancialOperations_Ids", $"[Id] <> {EmptyGuid} AND [UserId] <> {EmptyGuid} AND [BatchId] <> {EmptyGuid}");
            t.HasCheckConstraint("CK_FinancialOperations_Currency", Currency());
            t.HasCheckConstraint("CK_FinancialOperations_Reason", OptionalReason());
            t.HasCheckConstraint("CK_FinancialOperations_Shape", """
                (
                  [Kind] IN (1,2,3,4) AND [Amount] IS NOT NULL AND [Amount] > 0
                  AND [OriginalOperationId] IS NULL AND [OriginalOperationKind] IS NULL
                  AND (
                    ([Kind] IN (1,2) AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NOT NULL)
                    OR ([Kind] = 3 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NULL)
                    OR ([Kind] = 4 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NOT NULL AND [SourceAccountId] <> [DestinationAccountId])
                  )
                  AND ([Kind] <> 1 OR [Reason] IS NOT NULL)
                )
                OR (
                  [Kind] = 5 AND [Amount] IS NULL AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NULL
                  AND [OriginalOperationId] IS NOT NULL AND [OriginalOperationId] <> [Id]
                  AND [OriginalOperationKind] IS NOT NULL AND [OriginalOperationKind] IN (1,2,3,4)
                  AND [Reason] IS NOT NULL
                )
                """);
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.UserId, x.Currency, x.Id, x.Kind });
        b.HasAlternateKey(x => new { x.UserId, x.Currency, x.BatchId, x.Id, x.Kind });
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.OriginalOperationKind).HasConversion<int>();
        b.Property(x => x.Currency).HasColumnType("varchar(3)").IsRequired();
        b.Property(x => x.Amount).HasPrecision(28, 2);
        b.Property(x => x.Reason).HasMaxLength(FinancialOperation.MaximumReasonLength);
        b.HasOne<FinancialPostingBatchRecord>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.BatchId }).HasPrincipalKey(x => new { x.UserId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Account>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.Currency, x.SourceAccountId })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.Id }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Account>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.Currency, x.DestinationAccountId })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.Id }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<FinancialOperationRecord>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.Currency, x.OriginalOperationId, x.OriginalOperationKind })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.Id, x.Kind }).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.UserId, x.OriginalOperationId }).IsUnique()
            .HasFilter("[OriginalOperationId] IS NOT NULL");
        b.HasIndex(x => new { x.UserId, x.Currency, x.SourceAccountId, x.BatchId });
        b.HasIndex(x => new { x.UserId, x.Currency, x.DestinationAccountId, x.BatchId });
        ImmutableProperties(b);
    }

    private static void ConfigureOpening(EntityTypeBuilder<FinancialOpeningVersionRecord> b)
    {
        b.ToTable("FinancialOpeningVersions", t =>
        {
            JournalTable(t, "FinancialOpeningVersions");
            t.HasCheckConstraint("CK_FinancialOpeningVersions_Currency", Currency());
            t.HasCheckConstraint("CK_FinancialOpeningVersions_Revision", """
                ([Revision] = 1 AND [PreviousRevision] IS NULL)
                OR ([Revision] > 1 AND [PreviousRevision] IS NOT NULL AND [PreviousRevision] = [Revision] - 1)
                """);
            t.HasCheckConstraint("CK_FinancialOpeningVersions_Opening", """
                ([OpeningOperationId] IS NULL AND [OpeningOperationKind] IS NULL)
                OR ([OpeningOperationId] IS NOT NULL AND [OpeningOperationKind] IS NOT NULL AND [OpeningOperationKind] = 1)
                """);
            t.HasCheckConstraint("CK_FinancialOpeningVersions_Reason", RequiredReason());
        });
        b.HasKey(x => new { x.UserId, x.Currency, x.AccountId, x.Revision });
        b.Property(x => x.Currency).HasColumnType("varchar(3)").IsRequired();
        b.Property(x => x.Revision).ValueGeneratedNever();
        b.Property(x => x.OpeningOperationKind).HasConversion<int>();
        b.Property(x => x.Reason).HasMaxLength(FinancialOperation.MaximumReasonLength).IsRequired();
        b.HasOne<Account>().WithMany().HasForeignKey(x => new { x.UserId, x.Currency, x.AccountId })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.Id }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<FinancialPostingBatchRecord>().WithMany().HasForeignKey(x => new { x.UserId, x.BatchId })
            .HasPrincipalKey(x => new { x.UserId, x.Id }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<FinancialOpeningVersionRecord>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.Currency, x.AccountId, x.PreviousRevision })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.AccountId, x.Revision }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<FinancialOperationRecord>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.Currency, x.BatchId, x.OpeningOperationId, x.OpeningOperationKind })
            .HasPrincipalKey(x => new { x.UserId, x.Currency, x.BatchId, x.Id, x.Kind }).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.UserId, x.OpeningOperationId }).IsUnique().HasFilter("[OpeningOperationId] IS NOT NULL");
        ImmutableProperties(b);
    }

    private static void ConfigureOutcome(EntityTypeBuilder<FinancialRequestOutcomeRecord> b)
    {
        b.ToTable("FinancialRequestOutcomes", t =>
        {
            JournalTable(t, "FinancialRequestOutcomes");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Ids", $"[Id] <> {EmptyGuid} AND [UserId] <> {EmptyGuid}");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Command", "[CommandType] IN (1,2,3,4,5,6)");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Key",
                "DATALENGTH([IdempotencyKey]) BETWEEN 1 AND 128 AND [IdempotencyKey] NOT LIKE '%[^A-Za-z0-9_-]%' COLLATE Latin1_General_100_BIN2");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Fingerprint",
                "DATALENGTH([PayloadFingerprint]) = 64 AND [PayloadFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%' AND [FingerprintVersion] > 0");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Result",
                "([OutcomeKind] = 1 AND [BatchId] IS NOT NULL) OR ([OutcomeKind] = 2 AND [BatchId] IS NULL)");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Receipt", "ISJSON([ReceiptJson]) = 1 AND DATALENGTH([ReceiptJson]) <= 32768");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Code", "DATALENGTH([ResultCode]) BETWEEN 1 AND 64 AND [ResultCode] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'");
            t.HasCheckConstraint("CK_FinancialRequestOutcomes_Utc", Utc("[RecordedAtUtc]"));
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.UserId, x.Id });
        b.Property(x => x.CommandType).HasConversion<int>();
        b.Property(x => x.OutcomeKind).HasConversion<int>();
        b.Property(x => x.IdempotencyKey).HasColumnType("varchar(128)").UseCollation("Latin1_General_100_BIN2").IsRequired();
        b.Property(x => x.PayloadFingerprint).HasColumnType("char(64)").IsRequired();
        b.Property(x => x.ResultCode).HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.ReceiptJson).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.RecordedAtUtc).HasColumnType("datetimeoffset(7)");
        b.HasIndex(x => new { x.UserId, x.CommandType, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.UserId, x.BatchId }).IsUnique().HasFilter("[BatchId] IS NOT NULL");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<FinancialPostingBatchRecord>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.CommandType, x.BatchId })
            .HasPrincipalKey(x => new { x.UserId, x.CommandType, x.Id }).OnDelete(DeleteBehavior.NoAction);
        ImmutableProperties(b);
    }

    private static void ConfigureAudit(EntityTypeBuilder<FinancialAuditEntryRecord> b)
    {
        b.ToTable("FinancialAuditEntries", t =>
        {
            JournalTable(t, "FinancialAuditEntries");
            t.HasCheckConstraint("CK_FinancialAuditEntries_Ids", $"[Id] <> {EmptyGuid} AND [UserId] <> {EmptyGuid}");
            t.HasCheckConstraint("CK_FinancialAuditEntries_Utc", Utc("[RecordedAtUtc]"));
            t.HasCheckConstraint("CK_FinancialAuditEntries_Action", "DATALENGTH([Action]) BETWEEN 1 AND 64 AND [Action] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'");
            t.HasCheckConstraint("CK_FinancialAuditEntries_Summary", "ISJSON([SummaryJson]) = 1 AND DATALENGTH([SummaryJson]) <= 8192");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RecordedAtUtc).HasColumnType("datetimeoffset(7)");
        b.Property(x => x.Action).HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.SummaryJson).HasColumnType("nvarchar(max)").IsRequired();
        b.HasOne<FinancialRequestOutcomeRecord>().WithMany().HasForeignKey(x => new { x.UserId, x.OutcomeId })
            .HasPrincipalKey(x => new { x.UserId, x.Id }).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.UserId, x.OutcomeId }).IsUnique();
        ImmutableProperties(b);
    }

    private static void ConfigureMovement(EntityTypeBuilder<FinancialAccountMovement> b)
    {
        b.HasNoKey().ToView("FinancialAccountMovements");
        b.Property(x => x.Currency).HasColumnType("varchar(3)");
        b.Property(x => x.SignedAmount).HasPrecision(28, 2);
        b.Property(x => x.RecordedAtUtc).HasColumnType("datetimeoffset(7)");
        b.Property(x => x.EffectiveDate).HasColumnType("date");
        b.Property(x => x.Kind).HasConversion<int>();
    }

    private static void JournalTable(TableBuilder table, string name)
    {
        table.HasTrigger($"TR_{name}_AppendOnly");
        table.UseSqlOutputClause(false);
    }

    private static void ImmutableProperties<TEntity>(EntityTypeBuilder<TEntity> b) where TEntity : class
    {
        foreach (var property in b.Metadata.GetProperties())
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }

    private static string Utc(string column) =>
        $"DATEPART(TZOFFSET, {column}) = 0 AND {column} > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')";

    private static string Currency() =>
        "[Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP','USD') AND DATALENGTH([Currency]) = 3";

    private static string RequiredReason() =>
        $"DATALENGTH([Reason]) BETWEEN 2 AND 400 AND DATALENGTH([Reason]) = DATALENGTH(TRIM({TrimCharacters} FROM [Reason]))";

    private static string OptionalReason() => $"[Reason] IS NULL OR ({RequiredReason()})";
}
