using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;

namespace VaultFinance.Domain.FinancialOperations;

/// <summary>
/// An immutable operation descriptor. Creation validates local facts, not funds,
/// global reversal uniqueness, or successful persistence.
/// </summary>
public sealed class FinancialOperation
{
    public const int MaximumReasonLength = 200;

    public Guid Id { get; }
    public Guid UserId { get; }
    public FinancialPostingContext Posting { get; }
    public FinancialOperationKind Kind { get; }
    public FinancialOperationStatus Status => FinancialOperationStatus.Confirmed;
    public FinancialAmount? Amount { get; }
    public string Currency { get; }
    public Guid? SourceAccountId { get; }
    public Guid? DestinationAccountId { get; }
    public Guid? OriginalOperationId { get; }
    public FinancialOperationKind? OriginalOperationKind { get; }
    public string? Reason { get; }

    private FinancialOperation(
        Guid id, Guid userId, FinancialPostingContext posting,
        FinancialOperationKind kind, string currency, FinancialAmount? amount,
        Guid? sourceAccountId, Guid? destinationAccountId,
        Guid? originalOperationId, FinancialOperationKind? originalOperationKind,
        string? reason)
    {
        if (id == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("Operation and owner identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(posting);

        Id = id;
        UserId = userId;
        Posting = posting;
        Kind = kind;
        Currency = currency;
        Amount = amount;
        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        OriginalOperationId = originalOperationId;
        OriginalOperationKind = originalOperationKind;
        Reason = NormalizeReason(reason, kind is FinancialOperationKind.Opening or FinancialOperationKind.Reversal);
    }

    public static FinancialOperation CreateOpening(
        Guid id, Guid userId, Account account, FinancialAmount amount,
        FinancialPostingContext posting, string reason) =>
        CreateSingleAccount(id, userId, account, amount, posting, FinancialOperationKind.Opening, reason);

    public static FinancialOperation CreateIncome(
        Guid id, Guid userId, Account account, FinancialAmount amount,
        FinancialPostingContext posting, string? reason = null) =>
        CreateSingleAccount(id, userId, account, amount, posting, FinancialOperationKind.Income, reason);

    public static FinancialOperation CreateExpense(
        Guid id, Guid userId, Account account, FinancialAmount amount,
        FinancialPostingContext posting, string? reason = null) =>
        CreateSingleAccount(id, userId, account, amount, posting, FinancialOperationKind.Expense, reason);

    public static FinancialOperation CreateTransfer(
        Guid id, Guid userId, Account source, Account destination,
        FinancialAmount amount, FinancialPostingContext posting, string? reason = null)
    {
        ValidateAccount(source, userId, amount);
        ValidateAccount(destination, userId, amount);

        if (source.Id == destination.Id)
        {
            throw new DomainException("Transfer accounts must be distinct.");
        }

        return new FinancialOperation(id, userId, posting, FinancialOperationKind.Transfer,
            amount.Value.Currency, amount, source.Id, destination.Id, null, null, reason);
    }

    public static FinancialOperation CreateReversal(
        Guid id, Guid userId, FinancialOperation original,
        FinancialPostingContext posting, string reason)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(posting);

        if (userId != original.UserId)
        {
            throw new DomainException("The original operation must belong to the operation owner.");
        }

        if (original.Kind == FinancialOperationKind.Reversal)
        {
            throw new DomainException("A reversal cannot itself be reversed.");
        }

        if (id == original.Id || posting.BatchId == original.Posting.BatchId)
        {
            throw new DomainException("A reversal requires distinct operation and posting batch identifiers.");
        }

        if (posting.RecordedAtUtc < original.Posting.RecordedAtUtc)
        {
            throw new DomainException("A reversal cannot precede its original operation.");
        }

        // The immutable original supplies the inverse. No mutable copy of its financial fields.
        return new FinancialOperation(id, userId, posting, FinancialOperationKind.Reversal,
            original.Currency, null, null, null, original.Id, original.Kind, reason);
    }

    private static FinancialOperation CreateSingleAccount(
        Guid id, Guid userId, Account account, FinancialAmount amount,
        FinancialPostingContext posting, FinancialOperationKind kind, string? reason)
    {
        ValidateAccount(account, userId, amount);
        bool isExpense = kind == FinancialOperationKind.Expense;

        return new FinancialOperation(id, userId, posting, kind, amount.Value.Currency, amount,
            isExpense ? account.Id : null, isExpense ? null : account.Id, null, null, reason);
    }

    private static void ValidateAccount(Account account, Guid userId, FinancialAmount amount)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(amount);

        if (account.UserId != userId)
        {
            throw new DomainException("The account must belong to the operation owner.");
        }

        if (account.Status != AccountStatus.Active)
        {
            throw new DomainException("Ordinary financial operations require active accounts.");
        }

        if (account.Currency != amount.Value.Currency)
        {
            throw new DomainException("Account and operation currencies must match.");
        }
    }

    private static string? NormalizeReason(string? reason, bool required)
    {
        string? normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        if (required && normalized is null)
        {
            throw new DomainException("A reason is required for openings and reversals.");
        }

        if (normalized?.Length > MaximumReasonLength)
        {
            throw new DomainException("The operation reason exceeds the supported length.");
        }

        return normalized;
    }
}
