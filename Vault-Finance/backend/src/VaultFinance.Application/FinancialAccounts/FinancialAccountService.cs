using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.Application.FinancialAccounts;

public sealed class FinancialAccountService(IFinancialJournal journal) : IFinancialAccountService
{
    public Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, FinancialEntryRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default) =>
        RecordEntryAsync(owner, request, idempotencyKey, true, cancellationToken);

    public Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, FinancialEntryRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default) =>
        RecordEntryAsync(owner, request, idempotencyKey, false, cancellationToken);

    public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, InitializeOpeningRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        ValidateIdentityAndKey(request.AccountId, idempotencyKey, errors);

        Money? money = null;
        try
        {
            money = new Money(request.Amount, request.Currency!);
            if (money.Amount < 0m || money.Amount > FinancialAmount.MaximumValue)
            {
                errors["amount"] = ["Opening amounts must be nonnegative and within the financial limit."];
            }
        }
        catch (DomainException exception) { errors["money"] = [exception.Message]; }

        string? reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > FinancialOperation.MaximumReasonLength)
        {
            errors["reason"] = ["A reason of 1-200 characters is required."];
        }

        return errors.Count != 0
            ? Task.FromResult(new OpeningOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors))
            : journal.InitializeOpeningAsync(owner,
                new ValidatedOpening(request.AccountId, money!.Amount, money.Currency, reason!, idempotencyKey),
                cancellationToken);
    }

    public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return accountId == Guid.Empty
            ? Task.FromResult(new BalanceOutcome(Failure: FinancialFailure.InvalidInput))
            : journal.GetBalanceAsync(owner, accountId, cancellationToken);
    }

    private Task<FinancialEntryOutcome> RecordEntryAsync(FinancialOwner owner, FinancialEntryRequest request,
        string key, bool income, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        ValidateIdentityAndKey(request.AccountId, key, errors);
        FinancialAmount? amount = null;
        try { amount = new FinancialAmount(new Money(request.Amount, request.Currency!)); }
        catch (DomainException exception) { errors["money"] = [exception.Message]; }

        string? reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > FinancialOperation.MaximumReasonLength)
            errors["reason"] = ["A reason cannot exceed 200 characters."];
        if (errors.Count != 0)
            return Task.FromResult(new FinancialEntryOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors));

        var entry = new ValidatedFinancialEntry(request.AccountId, amount!.Value.Amount, amount.Value.Currency, reason, key);
        return income ? journal.RecordIncomeAsync(owner, entry, cancellationToken)
            : journal.RecordExpenseAsync(owner, entry, cancellationToken);
    }

    private static void ValidateIdentityAndKey(Guid accountId, string key, Dictionary<string, string[]> errors)
    {
        if (accountId == Guid.Empty) { errors["accountId"] = ["An account identifier is required."]; }
        if (string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            errors["idempotencyKey"] = ["Use 1-128 ASCII letters, digits, hyphens or underscores."];
    }
}
