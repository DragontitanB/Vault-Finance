using Microsoft.EntityFrameworkCore.Migrations;

namespace VaultFinance.Infrastructure.Persistence.Migrations;

public partial class FinancialJournalPersistence
{
    private static void AddJournalSqlObjects(MigrationBuilder migrationBuilder)
    {
        // Frozen migration SQL: do not delegate historical DDL to mutable application helpers.
        foreach (var table in new[]
        {
            "FinancialPostingBatches", "FinancialOperations", "FinancialOpeningVersions",
            "FinancialRequestOutcomes", "FinancialAuditEntries"
        })
        {
            ExecuteDdl(migrationBuilder, $"""
                CREATE TRIGGER [dbo].[TR_{table}_AppendOnly]
                ON [dbo].[{table}]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51001, 'Financial journal history is append-only.', 1;
                END;
                """);
        }

        ExecuteDdl(migrationBuilder, """
            CREATE VIEW [dbo].[FinancialAccountMovements]
            AS
            WITH OrdinaryEffects AS (
                SELECT [Id] AS [OperationId], [UserId], [DestinationAccountId] AS [AccountId],
                       [Currency], [BatchId], [Kind], [Amount] AS [SignedAmount]
                FROM [dbo].[FinancialOperations]
                WHERE [Kind] IN (1,2,4)
                UNION ALL
                SELECT [Id], [UserId], [SourceAccountId], [Currency], [BatchId], [Kind],
                       CAST(-[Amount] AS decimal(28,2))
                FROM [dbo].[FinancialOperations]
                WHERE [Kind] IN (3,4)
            ),
            AllEffects AS (
                SELECT [OperationId], [UserId], [AccountId], [Currency], [BatchId], [Kind], [SignedAmount]
                FROM OrdinaryEffects
                UNION ALL
                SELECT r.[Id], r.[UserId], e.[AccountId], r.[Currency], r.[BatchId], r.[Kind],
                       CAST(-e.[SignedAmount] AS decimal(28,2))
                FROM [dbo].[FinancialOperations] r
                JOIN OrdinaryEffects e ON e.[OperationId] = r.[OriginalOperationId] AND e.[UserId] = r.[UserId]
                WHERE r.[Kind] = 5
            )
            SELECT e.[OperationId], e.[UserId], e.[AccountId], e.[Currency], e.[BatchId],
                   b.[Sequence] AS [BatchSequence], b.[RecordedAtUtc], b.[EffectiveDate], e.[Kind], e.[SignedAmount]
            FROM AllEffects e
            JOIN [dbo].[FinancialPostingBatches] b ON b.[Id] = e.[BatchId] AND b.[UserId] = e.[UserId];
            """);
    }

    // CREATE VIEW/TRIGGER must start their own batch, including in idempotent IF guards.
    private static void ExecuteDdl(MigrationBuilder migrationBuilder, string sql) =>
        migrationBuilder.Sql("EXEC(N'" + sql.Replace("'", "''") + "');");
}
