using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialInspection;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

public sealed class SqlJournalInspectionReader(VaultFinanceDbContext context) : IJournalInspectionReader
{
    private readonly string connectionString = context.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Diagnostic persistence requires a configured connection.");

    public async Task<JournalCapture> CaptureAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.CaptureFinancialJournalInspection", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 20 };
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = ownerId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new(ErrorCode: "inspection.capture_incomplete");
            Guid inspection = reader.GetGuid(0), capturedOwner = reader.GetGuid(1);
            var capturedAt = reader.GetFieldValue<DateTimeOffset>(2); string mode = reader.GetString(3); long expectedRows = reader.GetInt64(4);
            if (capturedOwner != ownerId || mode != "shared-table-locks-v1" || expectedRows is < 0 or > 20000) return new(ErrorCode: "inspection.scope_invalid");
            var accounts = await Rows(r => new JournalAccount(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3)));
            var batches = await Rows(r => new JournalBatch(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), DateOnly.FromDateTime(r.GetDateTime(5)), r.GetString(6)));
            var operations = await Rows(r => new JournalOperation(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), Decimal(r, 5), GuidValue(r, 6), GuidValue(r, 7), GuidValue(r, 8), Integer(r, 9), Text(r, 10)));
            var openings = await Rows(r => new JournalOpening(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetInt32(3), Integer(r, 4), r.GetGuid(5), GuidValue(r, 6), Integer(r, 7), r.GetString(8)));
            var outcomes = await Rows(r => new JournalOutcome(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), GuidValue(r, 7), r.GetString(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10)));
            var audits = await Rows(r => new JournalAudit(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.GetString(5)));
            var effects = await Rows(r => new JournalEffect(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetInt64(5), r.GetFieldValue<DateTimeOffset>(6), DateOnly.FromDateTime(r.GetDateTime(7)), r.GetInt32(8), r.GetDecimal(9)));
            var balances = new List<JournalReportedBalance>();
            foreach (var _ in accounts)
            {
                var one = await Rows(r => new JournalReportedBalance(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetBoolean(2), Decimal(r, 3), Text(r, 4)));
                if (one.Count != 1) return new(ErrorCode: "inspection.capture_incomplete");
                balances.Add(one[0]);
            }
            if (!await reader.NextResultAsync(cancellationToken) || !await reader.ReadAsync(cancellationToken) ||
                reader.GetGuid(0) != inspection || reader.GetGuid(1) != ownerId || !reader.GetBoolean(2) || reader.GetInt64(3) != expectedRows ||
                expectedRows != accounts.Count + batches.Count + operations.Count + openings.Count + outcomes.Count + audits.Count + effects.Count + balances.Count ||
                await reader.ReadAsync(cancellationToken) || await reader.NextResultAsync(cancellationToken))
                return new(ErrorCode: "inspection.capture_incomplete");
            return new(new(inspection, ownerId, capturedAt, mode, true, accounts, batches, operations, openings, outcomes, audits, effects, balances));

            async Task<List<T>> Rows<T>(Func<SqlDataReader, T> map)
            {
                if (!await reader.NextResultAsync(cancellationToken)) throw new InvalidOperationException("Incomplete diagnostic result stream.");
                var result = new List<T>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (result.Count >= 20000) throw new InvalidOperationException("Oversized diagnostic stream.");
                    result.Add(map(reader));
                }
                return result;
            }
        }
        catch (OperationCanceledException) { return new(ErrorCode: "inspection.cancelled"); }
        catch (SqlException exception)
        {
            if (cancellationToken.IsCancellationRequested) return new(ErrorCode: "inspection.cancelled");
            return new(ErrorCode: exception.Number switch { 51040 => "inspection.limit_exceeded", 229 => "inspection.permission_denied",
                51010 => "inspection.invalid_context", -2 or 1205 or 1222 => "inspection.unavailable", _ => "inspection.read_failed" });
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or OverflowException or FormatException)
        { return new(ErrorCode: "inspection.capture_incomplete"); }
    }
    private static Guid? GuidValue(SqlDataReader r, int column) => r.IsDBNull(column) ? null : r.GetGuid(column);
    private static int? Integer(SqlDataReader r, int column) => r.IsDBNull(column) ? null : r.GetInt32(column);
    private static decimal? Decimal(SqlDataReader r, int column) => r.IsDBNull(column) ? null : r.GetDecimal(column);
    private static string? Text(SqlDataReader r, int column) => r.IsDBNull(column) ? null : r.GetString(column);
}
