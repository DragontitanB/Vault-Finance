using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Authentication;

internal sealed class AuthenticationApiFactory(SqlConnection connection, SqlTransaction transaction)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<VaultFinanceDbContext>();
            services.RemoveAll<DbContextOptions<VaultFinanceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<VaultFinanceDbContext>>();
            services.AddDbContext<VaultFinanceDbContext>(options =>
                options.UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150)));
            services.Replace(ServiceDescriptor.Scoped(provider =>
            {
                var context = new VaultFinanceDbContext(provider.GetRequiredService<DbContextOptions<VaultFinanceDbContext>>());
                context.Database.UseTransaction(transaction);
                return context;
            }));
            services.AddSingleton<IStartupFilter, TestClientAddressFilter>();
        });
    }

    // TestServer has no network peer. This test-only hook simulates distinct transport IPs.
    private sealed class TestClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Client-IP"], out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                }
                await nextMiddleware();
            });
            next(app);
        };
    }
}
