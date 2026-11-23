using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        var connectionString = SqlTestDatabase.ReadConnectionString();
        await SqlTestDatabase.EnsureReadyAsync(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        // Keep production's strict VaultFinanceDb guard; replace its registration only in tests.
        services.RemoveAll<VaultFinanceDbContext>();
        services.RemoveAll<DbContextOptions<VaultFinanceDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<VaultFinanceDbContext>>();
        services.AddDbContext<VaultFinanceDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(150)));
        Provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }
    }
}
