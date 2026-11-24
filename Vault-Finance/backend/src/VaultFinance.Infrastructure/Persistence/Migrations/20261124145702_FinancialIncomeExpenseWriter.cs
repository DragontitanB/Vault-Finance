using Microsoft.EntityFrameworkCore.Migrations;

using System;
using System.IO;
using System.Text.RegularExpressions;

#nullable disable

namespace VaultFinance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialIncomeExpenseWriter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(FinancialIncomeExpenseWriter).Assembly.GetManifestResourceStream(
                "VaultFinance.Infrastructure.Persistence.Migrations.Sql.FinancialIncomeExpenseWriter.sql")
                ?? throw new InvalidOperationException("The frozen financial writer SQL resource is missing.");
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
