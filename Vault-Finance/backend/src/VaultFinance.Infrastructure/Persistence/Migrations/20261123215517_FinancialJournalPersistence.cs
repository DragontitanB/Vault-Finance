using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VaultFinance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialJournalPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Accounts_UserId_Currency_Id",
                table: "Accounts",
                columns: new[] { "UserId", "Currency", "Id" });

            migrationBuilder.CreateTable(
                name: "FinancialPostingBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CommandType = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AccountingTimeZone = table.Column<string>(type: "varchar(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialPostingBatches", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_FinancialPostingBatches_UserId_CommandType_Id", x => new { x.UserId, x.CommandType, x.Id });
                    table.UniqueConstraint("AK_FinancialPostingBatches_UserId_Id", x => new { x.UserId, x.Id });
                    table.CheckConstraint("CK_FinancialPostingBatches_Command", "[CommandType] IN (1,2,3,4,5,6)");
                    table.CheckConstraint("CK_FinancialPostingBatches_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_FinancialPostingBatches_Utc", "DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')");
                    table.CheckConstraint("CK_FinancialPostingBatches_Zone", "[AccountingTimeZone] COLLATE Latin1_General_100_BIN2 = 'America/Santo_Domingo' AND DATALENGTH([AccountingTimeZone]) = 21");
                    table.ForeignKey(
                        name: "FK_FinancialPostingBatches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FinancialOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,2)", precision: 28, scale: 2, nullable: true),
                    SourceAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginalOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginalOperationKind = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialOperations", x => x.Id);
                    table.UniqueConstraint("AK_FinancialOperations_UserId_Currency_BatchId_Id_Kind", x => new { x.UserId, x.Currency, x.BatchId, x.Id, x.Kind });
                    table.UniqueConstraint("AK_FinancialOperations_UserId_Currency_Id_Kind", x => new { x.UserId, x.Currency, x.Id, x.Kind });
                    table.CheckConstraint("CK_FinancialOperations_Currency", "[Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP','USD') AND DATALENGTH([Currency]) = 3");
                    table.CheckConstraint("CK_FinancialOperations_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000' AND [BatchId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_FinancialOperations_Reason", "[Reason] IS NULL OR (DATALENGTH([Reason]) BETWEEN 2 AND 400 AND DATALENGTH([Reason]) = DATALENGTH(TRIM(NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133) + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288) FROM [Reason])))");
                    table.CheckConstraint("CK_FinancialOperations_Shape", "(\n  [Kind] IN (1,2,3,4) AND [Amount] IS NOT NULL AND [Amount] > 0\n  AND [OriginalOperationId] IS NULL AND [OriginalOperationKind] IS NULL\n  AND (\n    ([Kind] IN (1,2) AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NOT NULL)\n    OR ([Kind] = 3 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NULL)\n    OR ([Kind] = 4 AND [SourceAccountId] IS NOT NULL AND [DestinationAccountId] IS NOT NULL AND [SourceAccountId] <> [DestinationAccountId])\n  )\n  AND ([Kind] <> 1 OR [Reason] IS NOT NULL)\n)\nOR (\n  [Kind] = 5 AND [Amount] IS NULL AND [SourceAccountId] IS NULL AND [DestinationAccountId] IS NULL\n  AND [OriginalOperationId] IS NOT NULL AND [OriginalOperationId] <> [Id]\n  AND [OriginalOperationKind] IS NOT NULL AND [OriginalOperationKind] IN (1,2,3,4)\n  AND [Reason] IS NOT NULL\n)");
                    table.ForeignKey(
                        name: "FK_FinancialOperations_Accounts_UserId_Currency_DestinationAccountId",
                        columns: x => new { x.UserId, x.Currency, x.DestinationAccountId },
                        principalTable: "Accounts",
                        principalColumns: new[] { "UserId", "Currency", "Id" });
                    table.ForeignKey(
                        name: "FK_FinancialOperations_Accounts_UserId_Currency_SourceAccountId",
                        columns: x => new { x.UserId, x.Currency, x.SourceAccountId },
                        principalTable: "Accounts",
                        principalColumns: new[] { "UserId", "Currency", "Id" });
                    table.ForeignKey(
                        name: "FK_FinancialOperations_FinancialOperations_UserId_Currency_OriginalOperationId_OriginalOperationKind",
                        columns: x => new { x.UserId, x.Currency, x.OriginalOperationId, x.OriginalOperationKind },
                        principalTable: "FinancialOperations",
                        principalColumns: new[] { "UserId", "Currency", "Id", "Kind" });
                    table.ForeignKey(
                        name: "FK_FinancialOperations_FinancialPostingBatches_UserId_BatchId",
                        columns: x => new { x.UserId, x.BatchId },
                        principalTable: "FinancialPostingBatches",
                        principalColumns: new[] { "UserId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "FinancialRequestOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandType = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadFingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    FingerprintVersion = table.Column<int>(type: "int", nullable: false),
                    OutcomeKind = table.Column<int>(type: "int", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultCode = table.Column<string>(type: "varchar(64)", nullable: false),
                    ReceiptJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialRequestOutcomes", x => x.Id);
                    table.UniqueConstraint("AK_FinancialRequestOutcomes_UserId_Id", x => new { x.UserId, x.Id });
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Code", "DATALENGTH([ResultCode]) BETWEEN 1 AND 64 AND [ResultCode] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Command", "[CommandType] IN (1,2,3,4,5,6)");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Fingerprint", "DATALENGTH([PayloadFingerprint]) = 64 AND [PayloadFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%' AND [FingerprintVersion] > 0");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Key", "DATALENGTH([IdempotencyKey]) BETWEEN 1 AND 128 AND [IdempotencyKey] NOT LIKE '%[^A-Za-z0-9_-]%' COLLATE Latin1_General_100_BIN2");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Receipt", "ISJSON([ReceiptJson]) = 1 AND DATALENGTH([ReceiptJson]) <= 32768");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Result", "([OutcomeKind] = 1 AND [BatchId] IS NOT NULL) OR ([OutcomeKind] = 2 AND [BatchId] IS NULL)");
                    table.CheckConstraint("CK_FinancialRequestOutcomes_Utc", "DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')");
                    table.ForeignKey(
                        name: "FK_FinancialRequestOutcomes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_FinancialRequestOutcomes_FinancialPostingBatches_UserId_CommandType_BatchId",
                        columns: x => new { x.UserId, x.CommandType, x.BatchId },
                        principalTable: "FinancialPostingBatches",
                        principalColumns: new[] { "UserId", "CommandType", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "FinancialOpeningVersions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    PreviousRevision = table.Column<int>(type: "int", nullable: true),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpeningOperationKind = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialOpeningVersions", x => new { x.UserId, x.Currency, x.AccountId, x.Revision });
                    table.CheckConstraint("CK_FinancialOpeningVersions_Currency", "[Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP','USD') AND DATALENGTH([Currency]) = 3");
                    table.CheckConstraint("CK_FinancialOpeningVersions_Opening", "([OpeningOperationId] IS NULL AND [OpeningOperationKind] IS NULL)\nOR ([OpeningOperationId] IS NOT NULL AND [OpeningOperationKind] IS NOT NULL AND [OpeningOperationKind] = 1)");
                    table.CheckConstraint("CK_FinancialOpeningVersions_Reason", "DATALENGTH([Reason]) BETWEEN 2 AND 400 AND DATALENGTH([Reason]) = DATALENGTH(TRIM(NCHAR(9) + NCHAR(10) + NCHAR(11) + NCHAR(12) + NCHAR(13) + NCHAR(32) + NCHAR(133) + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + NCHAR(8193) + NCHAR(8194) + NCHAR(8195) + NCHAR(8196) + NCHAR(8197) + NCHAR(8198) + NCHAR(8199) + NCHAR(8200) + NCHAR(8201) + NCHAR(8202) + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288) FROM [Reason]))");
                    table.CheckConstraint("CK_FinancialOpeningVersions_Revision", "([Revision] = 1 AND [PreviousRevision] IS NULL)\nOR ([Revision] > 1 AND [PreviousRevision] IS NOT NULL AND [PreviousRevision] = [Revision] - 1)");
                    table.ForeignKey(
                        name: "FK_FinancialOpeningVersions_Accounts_UserId_Currency_AccountId",
                        columns: x => new { x.UserId, x.Currency, x.AccountId },
                        principalTable: "Accounts",
                        principalColumns: new[] { "UserId", "Currency", "Id" });
                    table.ForeignKey(
                        name: "FK_FinancialOpeningVersions_FinancialOpeningVersions_UserId_Currency_AccountId_PreviousRevision",
                        columns: x => new { x.UserId, x.Currency, x.AccountId, x.PreviousRevision },
                        principalTable: "FinancialOpeningVersions",
                        principalColumns: new[] { "UserId", "Currency", "AccountId", "Revision" });
                    table.ForeignKey(
                        name: "FK_FinancialOpeningVersions_FinancialOperations_UserId_Currency_BatchId_OpeningOperationId_OpeningOperationKind",
                        columns: x => new { x.UserId, x.Currency, x.BatchId, x.OpeningOperationId, x.OpeningOperationKind },
                        principalTable: "FinancialOperations",
                        principalColumns: new[] { "UserId", "Currency", "BatchId", "Id", "Kind" });
                    table.ForeignKey(
                        name: "FK_FinancialOpeningVersions_FinancialPostingBatches_UserId_BatchId",
                        columns: x => new { x.UserId, x.BatchId },
                        principalTable: "FinancialPostingBatches",
                        principalColumns: new[] { "UserId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "FinancialAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutcomeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", nullable: false),
                    SummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAuditEntries", x => x.Id);
                    table.CheckConstraint("CK_FinancialAuditEntries_Action", "DATALENGTH([Action]) BETWEEN 1 AND 64 AND [Action] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Za-z0-9_.-]%'");
                    table.CheckConstraint("CK_FinancialAuditEntries_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [UserId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_FinancialAuditEntries_Summary", "ISJSON([SummaryJson]) = 1 AND DATALENGTH([SummaryJson]) <= 8192");
                    table.CheckConstraint("CK_FinancialAuditEntries_Utc", "DATEPART(TZOFFSET, [RecordedAtUtc]) = 0 AND [RecordedAtUtc] > CONVERT(datetimeoffset(7), '0001-01-01T00:00:00+00:00')");
                    table.ForeignKey(
                        name: "FK_FinancialAuditEntries_FinancialRequestOutcomes_UserId_OutcomeId",
                        columns: x => new { x.UserId, x.OutcomeId },
                        principalTable: "FinancialRequestOutcomes",
                        principalColumns: new[] { "UserId", "Id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEntries_UserId_OutcomeId",
                table: "FinancialAuditEntries",
                columns: new[] { "UserId", "OutcomeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOpeningVersions_UserId_BatchId",
                table: "FinancialOpeningVersions",
                columns: new[] { "UserId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOpeningVersions_UserId_Currency_AccountId_PreviousRevision",
                table: "FinancialOpeningVersions",
                columns: new[] { "UserId", "Currency", "AccountId", "PreviousRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOpeningVersions_UserId_Currency_BatchId_OpeningOperationId_OpeningOperationKind",
                table: "FinancialOpeningVersions",
                columns: new[] { "UserId", "Currency", "BatchId", "OpeningOperationId", "OpeningOperationKind" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOpeningVersions_UserId_OpeningOperationId",
                table: "FinancialOpeningVersions",
                columns: new[] { "UserId", "OpeningOperationId" },
                unique: true,
                filter: "[OpeningOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOperations_UserId_BatchId",
                table: "FinancialOperations",
                columns: new[] { "UserId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOperations_UserId_Currency_DestinationAccountId_BatchId",
                table: "FinancialOperations",
                columns: new[] { "UserId", "Currency", "DestinationAccountId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOperations_UserId_Currency_OriginalOperationId_OriginalOperationKind",
                table: "FinancialOperations",
                columns: new[] { "UserId", "Currency", "OriginalOperationId", "OriginalOperationKind" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOperations_UserId_Currency_SourceAccountId_BatchId",
                table: "FinancialOperations",
                columns: new[] { "UserId", "Currency", "SourceAccountId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialOperations_UserId_OriginalOperationId",
                table: "FinancialOperations",
                columns: new[] { "UserId", "OriginalOperationId" },
                unique: true,
                filter: "[OriginalOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialPostingBatches_Sequence",
                table: "FinancialPostingBatches",
                column: "Sequence",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialPostingBatches_UserId_EffectiveDate_Sequence",
                table: "FinancialPostingBatches",
                columns: new[] { "UserId", "EffectiveDate", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialRequestOutcomes_UserId_BatchId",
                table: "FinancialRequestOutcomes",
                columns: new[] { "UserId", "BatchId" },
                unique: true,
                filter: "[BatchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialRequestOutcomes_UserId_CommandType_BatchId",
                table: "FinancialRequestOutcomes",
                columns: new[] { "UserId", "CommandType", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialRequestOutcomes_UserId_CommandType_IdempotencyKey",
                table: "FinancialRequestOutcomes",
                columns: new[] { "UserId", "CommandType", "IdempotencyKey" },
                unique: true);

            AddJournalSqlObjects(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Financial journal history is forward-only. Use a separately reviewed corrective migration.");
        }
    }
}
