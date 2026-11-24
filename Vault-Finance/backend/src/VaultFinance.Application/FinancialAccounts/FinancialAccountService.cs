using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.Application.FinancialAccounts;

public sealed class FinancialAccountService(IFinancialJournal journal) : IFinancialAccountService
{
    public Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, CorrectOpeningRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        ValidateIdentityAndKey(request.AccountId, idempotencyKey, errors);
        if (request.ExpectedRevision < 1) errors["expectedRevision"] = ["A positive expected opening revision is required."];
        Money? money = null;
        try
        {
            money = new Money(request.Amount, request.Currency!);
            if (money.Amount < 0m || money.Amount > FinancialAmount.MaximumValue)
                errors["amount"] = ["Opening amounts must be nonnegative and within the financial limit."];
        }
        catch (DomainException exception) { errors["money"] = [exception.Message]; }
        string? reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > FinancialOperation.MaximumReasonLength)
            errors["reason"] = ["A reason of 1-200 characters is required."];
        return errors.Count != 0
            ? Task.FromResult(new OpeningCorrectionOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors))
            : journal.CorrectOpeningAsync(owner, new(request.AccountId, money!.Amount, money.Currency,
                request.ExpectedRevision, reason!, idempotencyKey), cancellationToken);
    }

    public Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, FinancialReversalRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        ValidateKey(idempotencyKey, errors);
        if (request.OriginalOperationId == Guid.Empty)
            errors["originalOperationId"] = ["An original operation identifier is required."];
        string? reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > FinancialOperation.MaximumReasonLength)
            errors["reason"] = ["A reason of 1-200 characters is required."];
        return errors.Count != 0
            ? Task.FromResult(new FinancialReversalOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors))
            : journal.RecordReversalAsync(owner, new(request.OriginalOperationId, reason!, idempotencyKey), cancellationToken);
    }

    public Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, FinancialTransferRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        ValidateKey(idempotencyKey, errors);
        if (request.SourceAccountId == Guid.Empty) errors["sourceAccountId"] = ["A source account identifier is required."];
        if (request.DestinationAccountId == Guid.Empty) errors["destinationAccountId"] = ["A destination account identifier is required."];
        if (request.SourceAccountId == request.DestinationAccountId) errors["accounts"] = ["Transfer accounts must be distinct."];
        var amount = ValidatePositiveAmount(request.Amount, request.Currency, errors);
        string? reason = NormalizeOptionalReason(request.Reason, errors);
        if (errors.Count != 0)
            return Task.FromResult(new FinancialTransferOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors));
        return journal.RecordTransferAsync(owner, new(request.SourceAccountId, request.DestinationAccountId,
            amount!.Value.Amount, amount.Value.Currency, reason, idempotencyKey), cancellationToken);
    }

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
        var amount = ValidatePositiveAmount(request.Amount, request.Currency, errors);
        string? reason = NormalizeOptionalReason(request.Reason, errors);
        if (errors.Count != 0)
            return Task.FromResult(new FinancialEntryOutcome(Failure: FinancialFailure.InvalidInput, Errors: errors));

        var entry = new ValidatedFinancialEntry(request.AccountId, amount!.Value.Amount, amount.Value.Currency, reason, key);
        return income ? journal.RecordIncomeAsync(owner, entry, cancellationToken)
            : journal.RecordExpenseAsync(owner, entry, cancellationToken);
    }

    private static void ValidateIdentityAndKey(Guid accountId, string key, Dictionary<string, string[]> errors)
    {
        if (accountId == Guid.Empty) { errors["accountId"] = ["An account identifier is required."]; }
        ValidateKey(key, errors);
    }

    private static void ValidateKey(string key, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            errors["idempotencyKey"] = ["Use 1-128 ASCII letters, digits, hyphens or underscores."];
    }

    private static FinancialAmount? ValidatePositiveAmount(decimal amount, string? currency, Dictionary<string, string[]> errors)
    {
        try { return new FinancialAmount(new Money(amount, currency!)); }
        catch (DomainException exception) { errors["money"] = [exception.Message]; return null; }
    }

    private static string? NormalizeOptionalReason(string? value, Dictionary<string, string[]> errors)
    {
        string? reason = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (reason?.Length > FinancialOperation.MaximumReasonLength)
            errors["reason"] = ["A reason cannot exceed 200 characters."];
        return reason;
    }
}
