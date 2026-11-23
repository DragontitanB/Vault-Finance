using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Each attempt uses an independent connection; SQL owns the complete transaction.</summary>
public sealed class SqlFinancialJournal(VaultFinanceDbContext context) : IFinancialJournal
{
    private static readonly JsonSerializerOptions ReceiptOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = context.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Financial persistence requires a configured connection.");
    private const int MaximumAttempts = 3;

    public async Task<OpeningOutcome> InitializeOpeningAsync(
        FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken)
    {
        bool commitMayBeUnknown = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = Procedure(connection, "dbo.InitializeFinancialOpening", owner, opening.AccountId);
                // Preserve all meaningful decimal digits, including when a caller bypasses Application validation.
                command.Parameters.Add("@AmountText", SqlDbType.NVarChar, -1).Value =
                    opening.Amount.ToString("0.00##########################", CultureInfo.InvariantCulture);
                command.Parameters.Add("@Currency", SqlDbType.NVarChar, -1).Value = opening.Currency;
                command.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = opening.Reason;
                command.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, -1).Value = opening.IdempotencyKey;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) { throw new InvalidOperationException("The financial writer returned no result."); }
                string code = reader.GetString(0);
                if (code != "opening.initialized") { return new(Failure: Failure(code)); }
                var receipt = JsonSerializer.Deserialize<OpeningReceipt>(reader.GetString(1), ReceiptOptions);
                if (receipt is null || receipt.ReceiptVersion != 1 || receipt.AccountId != opening.AccountId ||
                    receipt.BatchId == Guid.Empty || receipt.Revision != 1 || receipt.Currency != opening.Currency ||
                    receipt.OpeningAmount != opening.Amount || receipt.RecordedAtUtc.Offset != TimeSpan.Zero ||
                    (receipt.OperationId is null) != (opening.Amount == 0m))
                {
                    return new(Failure: FinancialFailure.JournalInconsistent);
                }
                return new(receipt);
            }
            catch (SqlException exception) when (exception.Number is 51010)
            {
                return new(Failure: FinancialFailure.InvalidInput);
            }
            catch (SqlException exception) when (exception.Number is 51020 or 8115)
            {
                return new(Failure: FinancialFailure.JournalInconsistent);
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested && IsRetryable(exception))
            {
                commitMayBeUnknown |= IsAmbiguous(exception);
                if (attempt == MaximumAttempts)
                {
                    return new(Failure: commitMayBeUnknown
                        ? FinancialFailure.CommitOutcomeUnknown : FinancialFailure.TemporarilyUnavailable);
                }
                // A new connection retries the entire same-key protocol, never an isolated INSERT.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
            catch (JsonException)
            {
                return new(Failure: FinancialFailure.JournalInconsistent);
            }
        }
    }

    public async Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = Procedure(connection, "dbo.GetDerivedFinancialBalance", owner, accountId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) { throw new InvalidOperationException("The financial reader returned no result."); }
                string code = reader.GetString(0);
                return code != "balance.read"
                    ? new(Failure: Failure(code))
                    : new(new DerivedAccountBalance(accountId, reader.GetString(1), reader.GetBoolean(2),
                        reader.IsDBNull(3) ? null : reader.GetDecimal(3)));
            }
            catch (SqlException exception) when (exception.Number is 51020 or 8115)
            {
                return new(Failure: FinancialFailure.JournalInconsistent);
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested && IsRetryable(exception))
            {
                if (attempt == MaximumAttempts) { return new(Failure: FinancialFailure.TemporarilyUnavailable); }
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private static SqlCommand Procedure(SqlConnection connection, string name, FinancialOwner owner, Guid accountId)
    {
        var command = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 15 };
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = owner.UserId;
        command.Parameters.Add("@AccountId", SqlDbType.UniqueIdentifier).Value = accountId;
        return command;
    }

    private static FinancialFailure Failure(string code) => code switch
    {
        "account.not_found" => FinancialFailure.NotFound,
        "account.archived" => FinancialFailure.Archived,
        "account.currency_mismatch" => FinancialFailure.CurrencyMismatch,
        "opening.already_initialized" => FinancialFailure.AlreadyInitialized,
        "idempotency.conflict" => FinancialFailure.IdempotencyConflict,
        _ => FinancialFailure.JournalInconsistent
    };

    private static bool IsAmbiguous(SqlException exception) =>
        exception.Number is -2 or 0 or 20 or 64 or 233 or 10053 or 10054 or 10060 || exception.Class >= 20;

    private static bool IsRetryable(SqlException exception) =>
        exception.Number is 1205 or 1222 or 51030 or 2601 or 2627 || IsAmbiguous(exception);
}
