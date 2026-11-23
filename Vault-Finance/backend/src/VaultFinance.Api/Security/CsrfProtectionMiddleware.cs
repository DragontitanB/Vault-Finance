using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace VaultFinance.Api.Security;

public sealed class CsrfProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> options)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-cache, no-store";

        if (!context.Request.IsHttps)
        {
            await Results.Problem(statusCode: 400, title: "HTTPS is required.",
                extensions: new Dictionary<string, object?> { ["code"] = "https_required" }).ExecuteAsync(context);
            return;
        }

        var method = context.Request.Method;
        if (context.GetEndpoint() is not null &&
            !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) &&
            !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method))
        {
            var header = options.Value.HeaderName!;
            var valid = context.Request.Headers.TryGetValue(header, out var token) &&
                token.Count == 1 && !string.IsNullOrWhiteSpace(token[0]);
            if (valid)
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { valid = false; }
            }

            if (!valid)
            {
                await Results.Problem(statusCode: 400, title: "A valid antiforgery token is required.",
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token" }).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
