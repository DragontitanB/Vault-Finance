using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Infrastructure.Persistence.FinancialJournal;

/// <summary>Each attempt uses an independent connection; SQL owns the complete transaction.</summary>
public sealed class SqlFinancialJournal(VaultFinanceDbContext context) : IFinancialJournal
{
    private static readonly JsonSerializerOptions ReceiptOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = context.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Financial persistence requires a configured connection.");
    private const int MaximumAttempts = 3;

    public Task<OpeningOutcome> InitializeOpeningAsync(
        FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken) =>
        ExecuteWriteAsync("dbo.InitializeFinancialOpening", owner, opening.AccountId, opening.Amount,
            opening.Currency, opening.Reason, opening.IdempotencyKey,
            (code, json) =>
            {
                if (code != "opening.initialized") { return new OpeningOutcome(Failure: Failure(code)); }
                var receipt = JsonSerializer.Deserialize<OpeningReceipt>(json, ReceiptOptions);
                if (receipt is null || receipt.ReceiptVersion != 1 || receipt.AccountId != opening.AccountId ||
                    receipt.BatchId == Guid.Empty || receipt.Revision != 1 || receipt.Currency != opening.Currency ||
                    receipt.OpeningAmount != opening.Amount || receipt.RecordedAtUtc.Offset != TimeSpan.Zero ||
                    (receipt.OperationId is null) != (opening.Amount == 0m))
                    return new OpeningOutcome(Failure: FinancialFailure.JournalInconsistent);
                return new OpeningOutcome(receipt);
            },
            failure => new OpeningOutcome(Failure: failure), cancellationToken);

    public Task<FinancialEntryOutcome> RecordIncomeAsync(
        FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) =>
        RecordEntryAsync(owner, entry, FinancialOperationKind.Income, "dbo.RecordFinancialIncome", cancellationToken);

    public Task<FinancialEntryOutcome> RecordExpenseAsync(
        FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) =>
        RecordEntryAsync(owner, entry, FinancialOperationKind.Expense, "dbo.RecordFinancialExpense", cancellationToken);

    private Task<FinancialEntryOutcome> RecordEntryAsync(FinancialOwner owner, ValidatedFinancialEntry entry,
        FinancialOperationKind kind, string procedure, CancellationToken cancellationToken) =>
        ExecuteWriteAsync(procedure, owner, entry.AccountId, entry.Amount, entry.Currency, entry.Reason, entry.IdempotencyKey,
            (code, json) =>
            {
                string expectedCode = kind == FinancialOperationKind.Income ? "income.posted" : "expense.posted";
                if (code != expectedCode) { return new FinancialEntryOutcome(Failure: Failure(code)); }
                var receipt = JsonSerializer.Deserialize<FinancialEntryReceipt>(json, ReceiptOptions);
                if (receipt is null || receipt.ReceiptVersion != 1 || receipt.AccountId != entry.AccountId ||
                    receipt.BatchId == Guid.Empty || receipt.OperationId == Guid.Empty || receipt.Kind != kind ||
                    receipt.Currency != entry.Currency || receipt.Amount != entry.Amount ||
                    receipt.RecordedAtUtc.Offset != TimeSpan.Zero)
                    return new FinancialEntryOutcome(Failure: FinancialFailure.JournalInconsistent);
                return new FinancialEntryOutcome(receipt);
            },
            failure => new FinancialEntryOutcome(Failure: failure), cancellationToken);

    private async Task<TResult> ExecuteWriteAsync<TResult>(string procedure, FinancialOwner owner,
        Guid accountId, decimal amount, string currency, string? reason, string key,
        Func<string, string, TResult> decode, Func<FinancialFailure, TResult> failed, CancellationToken cancellationToken)
    {
        bool commitMayBeUnknown = false;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = Procedure(connection, procedure, owner, accountId);
                // Never round meaningful decimal digits before the SQL protocol validates them.
                command.Parameters.Add("@AmountText", SqlDbType.NVarChar, -1).Value =
                    amount.ToString("0.00##########################", CultureInfo.InvariantCulture);
                command.Parameters.Add("@Currency", SqlDbType.NVarChar, -1).Value = currency;
                command.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = (object?)reason ?? DBNull.Value;
                command.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, -1).Value = key;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) { throw new InvalidOperationException("The financial writer returned no result."); }
                return decode(reader.GetString(0), reader.GetString(1));
            }
            catch (SqlException exception) when (exception.Number is 51010)
            {
                return failed(FinancialFailure.InvalidInput);
            }
            catch (SqlException exception) when (exception.Number is 51020 or 8115)
            {
                return failed(FinancialFailure.JournalInconsistent);
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested && IsRetryable(exception))
            {
                commitMayBeUnknown |= IsAmbiguous(exception);
                if (attempt == MaximumAttempts)
                    return failed(commitMayBeUnknown ? FinancialFailure.CommitOutcomeUnknown : FinancialFailure.TemporarilyUnavailable);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
            catch (JsonException)
            {
                return failed(FinancialFailure.JournalInconsistent);
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
        "account.uninitialized" => FinancialFailure.Uninitialized,
        "funds.insufficient" => FinancialFailure.InsufficientFunds,
        "balance.limit_exceeded" => FinancialFailure.BalanceLimitExceeded,
        "idempotency.conflict" => FinancialFailure.IdempotencyConflict,
        _ => FinancialFailure.JournalInconsistent
    };

    private static bool IsAmbiguous(SqlException exception) =>
        exception.Number is -2 or 0 or 20 or 64 or 233 or 10053 or 10054 or 10060 || exception.Class >= 20;

    private static bool IsRetryable(SqlException exception) =>
        exception.Number is 1205 or 1222 or 51030 or 2601 or 2627 || IsAmbiguous(exception);
}
