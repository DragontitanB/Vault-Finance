namespace VaultFinance.Application.Authentication;

public enum AuthenticationFailure
{
    InvalidInput,
    DuplicateEmail,
    InvalidCredentials
}

public sealed record AuthenticationOutcome(
    AuthenticatedUser? User = null,
    AuthenticationFailure? Failure = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
