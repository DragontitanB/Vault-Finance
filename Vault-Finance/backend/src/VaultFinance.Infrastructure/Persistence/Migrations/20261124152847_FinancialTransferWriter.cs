using Microsoft.EntityFrameworkCore.Migrations;

using System.Text.RegularExpressions;

#nullable disable

namespace VaultFinance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialTransferWriter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(FinancialTransferWriter).Assembly.GetManifestResourceStream(
                "VaultFinance.Infrastructure.Persistence.Migrations.Sql.FinancialTransferWriter.sql")
                ?? throw new InvalidOperationException("The frozen financial transfer SQL resource is missing.");
            using var reader = new StreamReader(stream);
            foreach (var batch in Regex.Split(reader.ReadToEnd(), @"(?m)^\s*GO\s*$"))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                    migrationBuilder.Sql("EXEC(N'" + batch.Replace("'", "''") + "');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Financial history is forward-only. Use a reviewed corrective migration.");
        }
    }
}
