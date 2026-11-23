using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace VaultFinance.Api.Security;

public static class AuthenticationRateLimits
{
    public const string Login = "auth-login";
    public const string Register = "auth-register";

    public static IServiceCollection AddAuthenticationRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Login, context => Partition(context, 5));
            options.AddPolicy(Register, context => Partition(context, 3));
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }
                await Results.Problem(statusCode: 429, title: "Too many requests. Try again later.",
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limit_exceeded" })
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext context, int permits) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0,
                AutoReplenishment = true
            });
}
