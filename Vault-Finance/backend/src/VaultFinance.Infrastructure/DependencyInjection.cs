using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Configuration is validated only when persistence is resolved, not by process liveness requests.
        services.AddDbContext<VaultFinanceDbContext>(options =>
        {
            var connection = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required to use persistence.");
            }

            var target = new SqlConnectionStringBuilder(connection);
            if (!string.Equals(target.InitialCatalog, "VaultFinanceDb", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Persistence must target the VaultFinanceDb database.");
            }

            options.UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150));
        });

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            options.Stores.MaxLengthForKeys = 128)
            .AddEntityFrameworkStores<VaultFinanceDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-VaultFinance";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-VaultFinance.Antiforgery";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddAuthorization();

        return services;
    }
}
