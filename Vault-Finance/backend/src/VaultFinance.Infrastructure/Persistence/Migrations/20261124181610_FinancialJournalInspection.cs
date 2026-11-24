using Microsoft.EntityFrameworkCore.Migrations;

using System.Text.RegularExpressions;

#nullable disable

namespace VaultFinance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialJournalInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(FinancialJournalInspection).Assembly.GetManifestResourceStream(
                "VaultFinance.Infrastructure.Persistence.Migrations.Sql.FinancialJournalInspection.sql")
                ?? throw new InvalidOperationException("The frozen financial inspection SQL resource is missing.");
            using var reader = new StreamReader(stream);
            foreach (var batch in Regex.Split(reader.ReadToEnd(), @"(?m)^\s*GO\s*$"))
            {
                if (!string.IsNullOrWhiteSpace(batch)) migrationBuilder.Sql("EXEC(N'" + batch.Replace("'", "''") + "');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Financial history tooling is forward-only. Use a reviewed corrective migration.");
        }
    }
}
