namespace VaultFinance.Application.Authentication;

// Classes intentionally avoid record-generated ToString output containing credentials.
public sealed class RegisterRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
}

public sealed class LoginRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
}
