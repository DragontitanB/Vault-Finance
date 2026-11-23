using System.Security.Claims;
using VaultFinance.Application.Accounts;
using VaultFinance.Domain.Accounts;

namespace VaultFinance.Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/accounts").WithTags("Accounts").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id == Guid.Empty)
            {
                return Results.Problem(statusCode: 401, title: "Authentication is required.");
            }

            var request = context.HttpContext.Request;
            var isList = HttpMethods.IsGet(request.Method) &&
                string.Equals(request.Path.Value?.TrimEnd('/'), "/api/v1/accounts", StringComparison.OrdinalIgnoreCase);
            var allowed = isList ? new[] { "page", "pageSize", "status", "sort" } : [];
            if (request.Query.Any(entry => entry.Value.Count != 1 || !allowed.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["query"] = ["Only supported, non-repeated query parameters are allowed."]
                });
            }

            return await next(context);
        });

        group.MapPost("", async (CreateAccountRequest request, ClaimsPrincipal user,
            IAccountService accounts, CancellationToken cancellationToken) =>
        {
            var result = await accounts.CreateAsync(UserId(user), request, cancellationToken);
            return result.Failure is null
                ? Results.Created($"/api/v1/accounts/{result.Account!.Id}", result.Account)
                : ToResult(result);
        });

        group.MapGet("", async (int? page, int? pageSize, string? status, string? sort, ClaimsPrincipal user,
            IAccountService accounts, CancellationToken cancellationToken) =>
        {
            AccountStatus? filter = null;
            if (status is not null)
            {
                if (string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)) { filter = AccountStatus.Active; }
                else if (string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase)) { filter = AccountStatus.Archived; }
                else
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = ["Status must be Active or Archived."]
                    });
                }
            }

            var outcome = await accounts.ListAsync(UserId(user),
                new(page ?? 1, pageSize ?? 20, filter, sort ?? "name"), cancellationToken);
            return outcome.Errors is null ? Results.Ok(outcome.Page) : Results.ValidationProblem(
                outcome.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IAccountService accounts, CancellationToken cancellationToken) =>
            ToResult(await accounts.GetAsync(UserId(user), id, cancellationToken)));

        group.MapPatch("/{id:guid}", async (Guid id, RenameAccountRequest request, ClaimsPrincipal user,
            IAccountService accounts, CancellationToken cancellationToken) =>
            ToResult(await accounts.RenameAsync(UserId(user), id, request, cancellationToken)));

        group.MapPost("/{id:guid}/archive", async (Guid id, ArchiveAccountRequest? request, ClaimsPrincipal user,
            IAccountService accounts, CancellationToken cancellationToken) =>
            ToResult(await accounts.ArchiveAsync(UserId(user), id, cancellationToken)));

        return endpoints;
    }

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult ToResult(AccountOutcome outcome) => outcome.Failure switch
    {
        null => Results.Ok(outcome.Account),
        AccountFailure.InvalidInput => Results.ValidationProblem(outcome.Errors!.ToDictionary(entry => entry.Key, entry => entry.Value)),
        AccountFailure.NotFound => Results.Problem(statusCode: 404, title: "Account was not found.",
            extensions: new Dictionary<string, object?> { ["code"] = "account_not_found" }),
        AccountFailure.Archived => Results.Problem(statusCode: 409, title: "An archived account cannot be edited.",
            extensions: new Dictionary<string, object?> { ["code"] = "account_archived" }),
        _ => throw new InvalidOperationException("Unexpected account outcome.")
    };
}
