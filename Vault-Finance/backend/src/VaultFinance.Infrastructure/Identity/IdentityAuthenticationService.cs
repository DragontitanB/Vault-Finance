using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.Authentication;

namespace VaultFinance.Infrastructure.Identity;

internal sealed class IdentityAuthenticationService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    UnknownUserPasswordVerifier unknownUserPassword) : IAuthenticationService
{
    public async Task<AuthenticationOutcome> RegisterAsync(RegisterRequest request)
    {
        var email = request.Email?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (!IsValidEmail(email))
        {
            errors["email"] = ["A valid email of at most 254 characters is required."];
        }

        if (request.Password is null || request.Password.Length is < 15 or > 128)
        {
            errors["password"] = ["Password must contain between 15 and 128 characters."];
        }

        if (errors.Count != 0)
        {
            return new(Failure: AuthenticationFailure.InvalidInput, Errors: errors);
        }

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, request.Password!);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // UserName always equals email here. The existing unique normalized-name index closes the race.
            return new(Failure: AuthenticationFailure.DuplicateEmail);
        }

        if (result.Succeeded)
        {
            return new(new AuthenticatedUser(user.Id, user.Email!));
        }

        if (result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName"))
        {
            return new(Failure: AuthenticationFailure.DuplicateEmail);
        }

        var passwordErrors = result.Errors.Where(error => error.Code.StartsWith("Password", StringComparison.Ordinal))
            .Select(error => error.Description).ToArray();
        errors[passwordErrors.Length == 0 ? "email" : "password"] =
            passwordErrors.Length == 0 ? ["The supplied email cannot be registered."] : passwordErrors;
        return new(Failure: AuthenticationFailure.InvalidInput, Errors: errors);
    }

    public async Task<AuthenticationOutcome> LoginAsync(LoginRequest request)
    {
        var email = request.Email?.Trim();
        if (!IsValidEmail(email) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
        {
            return new(Failure: AuthenticationFailure.InvalidCredentials);
        }

        // Identity normalizes the lookup; registration binds UserName to email.
        var user = await users.FindByNameAsync(email!);
        if (user is null)
        {
            unknownUserPassword.Verify(request.Password);
            return new(Failure: AuthenticationFailure.InvalidCredentials);
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut || result.IsNotAllowed)
            {
                unknownUserPassword.Verify(request.Password);
            }
            return new(Failure: AuthenticationFailure.InvalidCredentials);
        }

        return new(new AuthenticatedUser(user.Id, user.Email!));
    }

    public Task LogoutAsync() => signIn.SignOutAsync();

    public async Task<AuthenticatedUser?> GetUserAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user?.Email is { } email ? new AuthenticatedUser(user.Id, email) : null;
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 254 && new EmailAddressAttribute().IsValid(email);
}
