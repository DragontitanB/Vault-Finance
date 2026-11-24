using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;
using VaultFinance.Application.Authentication;
using Microsoft.AspNetCore.DataProtection;
using VaultFinance.Application.Accounts;
using VaultFinance.Application.FinancialAccounts;
using VaultFinance.Application.FinancialInspection;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

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
        {
            options.Stores.MaxLengthForKeys = 128;
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 15;
            options.Password.RequiredUniqueChars = 4;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 10;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(1);
        })
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
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                    title: "Authentication is required.").ExecuteAsync(context.HttpContext);
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Access is forbidden.").ExecuteAsync(context.HttpContext);
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
        services.AddDataProtection().SetApplicationName("VaultFinance");
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        services.AddSingleton<UnknownUserPasswordVerifier>();
        services.AddScoped<IAuthenticationService, IdentityAuthenticationService>();
        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IFinancialJournal, SqlFinancialJournal>();
        services.AddScoped<IFinancialAccountService, FinancialAccountService>();
        services.AddScoped<IJournalInspectionReader, SqlJournalInspectionReader>();
        services.AddScoped<IFinancialInspectionService, FinancialInspectionService>();

        return services;
    }
}
