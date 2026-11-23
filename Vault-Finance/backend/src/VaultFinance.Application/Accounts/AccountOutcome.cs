namespace VaultFinance.Application.Accounts;

public enum AccountFailure
{
    InvalidInput,
    NotFound,
    Archived
}

public sealed record AccountOutcome(
    AccountResponse? Account = null,
    AccountFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record AccountListOutcome(
    AccountPage? Page = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
