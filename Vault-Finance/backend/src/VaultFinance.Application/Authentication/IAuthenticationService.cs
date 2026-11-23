namespace VaultFinance.Application.Authentication;

public interface IAuthenticationService
{
    Task<AuthenticationOutcome> RegisterAsync(RegisterRequest request);
    Task<AuthenticationOutcome> LoginAsync(LoginRequest request);
    Task LogoutAsync();
    Task<AuthenticatedUser?> GetUserAsync(Guid userId);
}
