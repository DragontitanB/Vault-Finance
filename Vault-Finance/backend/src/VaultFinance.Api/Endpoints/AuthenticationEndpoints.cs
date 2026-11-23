using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using VaultFinance.Api.Security;
using VaultFinance.Application.Authentication;

namespace VaultFinance.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();

        group.MapPost("/register", async (RegisterRequest request, IAuthenticationService authentication) =>
            ToResult(await authentication.RegisterAsync(request), registration: true))
            .AllowAnonymous().RequireRateLimiting(AuthenticationRateLimits.Register);

        group.MapPost("/login", async (LoginRequest request, IAuthenticationService authentication,
            HttpContext context, IOptions<AntiforgeryOptions> antiforgery) =>
        {
            var outcome = await authentication.LoginAsync(request);
            if (outcome.Failure is null) { ResetAntiforgeryCookie(context, antiforgery.Value); }
            return ToResult(outcome);
        }).AllowAnonymous().RequireRateLimiting(AuthenticationRateLimits.Login);

        group.MapPost("/logout", async (IAuthenticationService authentication, HttpContext context,
            IOptions<AntiforgeryOptions> antiforgery) =>
        {
            await authentication.LogoutAsync();
            ResetAntiforgeryCookie(context, antiforgery.Value);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal principal, IAuthenticationService authentication) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                return Results.Problem(statusCode: 401, title: "Authentication is required.");
            }
            var user = await authentication.GetUserAsync(id);
            return user is null
                ? Results.Problem(statusCode: 401, title: "Authentication is required.")
                : Results.Ok(user);
        }).RequireAuthorization();

        return endpoints;
    }

    private static IResult ToResult(AuthenticationOutcome outcome, bool registration = false) => outcome.Failure switch
    {
        AuthenticationFailure.InvalidInput => Results.ValidationProblem(
            outcome.Errors!.ToDictionary(entry => entry.Key, entry => entry.Value)),
        AuthenticationFailure.DuplicateEmail => Results.Problem(statusCode: 409,
            title: "The email is already registered.", extensions: new Dictionary<string, object?> { ["code"] = "duplicate_email" }),
        AuthenticationFailure.InvalidCredentials => Results.Problem(statusCode: 401,
            title: "Invalid email or password.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials" }),
        null => registration ? Results.Json(outcome.User, statusCode: 201) : Results.Ok(outcome.User),
        _ => throw new InvalidOperationException("Unexpected authentication outcome.")
    };

    private static void ResetAntiforgeryCookie(HttpContext context, AntiforgeryOptions options) =>
        context.Response.Cookies.Delete(options.Cookie.Name!, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict
        });
}
