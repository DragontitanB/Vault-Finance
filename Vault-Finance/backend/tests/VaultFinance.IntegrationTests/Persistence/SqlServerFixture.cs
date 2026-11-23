using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Infrastructure;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed class SqlServerFixture : IAsyncLifetime
{
    public ServiceProvider Provider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS") != "1")
        {
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var value = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("SQL tests require the API's DefaultConnection in User Secrets or environment configuration.");
        }

        var target = new SqlConnectionStringBuilder(value);
        if (target.InitialCatalog != "VaultFinanceDb" || !target.IntegratedSecurity ||
            !new[] { @"localhost\SQLEXPRESS", @"DRAGON\SQLEXPRESS", @".\SQLEXPRESS" }
                .Contains(target.DataSource, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQL tests are restricted to local SQLEXPRESS/VaultFinanceDb with Windows authentication.");
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        Provider = services.BuildServiceProvider();
        await using var scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await context.Database.OpenConnectionAsync();
        if (context.Database.GetDbConnection().Database != "VaultFinanceDb")
        {
            throw new InvalidOperationException("Unexpected SQL test database.");
        }

        if ((await context.Database.GetPendingMigrationsAsync()).Any())
        {
            throw new InvalidOperationException("Apply the reviewed migrations before running SQL tests. Tests never migrate or create databases.");
        }
    }

    public async Task DisposeAsync()
    {
        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }
    }
}
