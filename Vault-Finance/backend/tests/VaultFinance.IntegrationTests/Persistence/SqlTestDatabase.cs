using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

/// <summary>Explicit, retained test database; never falls back to the development database.</summary>
public static class SqlTestDatabase
{
    public const string DatabaseName = "VaultFinanceJournalTests";
    public const string OwnershipMarker = "VaultFinance.JournalPersistenceTests.v1";
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static readonly ServiceProvider IdentityModelOptions = new ServiceCollection()
        .AddOptions().Configure<IdentityOptions>(options => options.Stores.MaxLengthForKeys = 128)
        .BuildServiceProvider();

    public static string ValidateConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "SQL tests require ConnectionStrings:SqlTestConnection; DefaultConnection is never used.");
        }

        SqlConnectionStringBuilder target;
        try { target = new SqlConnectionStringBuilder(value); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The SQL test connection could not be parsed. Its value is not logged.");
        }

        if (target.InitialCatalog != DatabaseName || !target.IntegratedSecurity ||
            !string.IsNullOrEmpty(target.AttachDBFilename) || target.UserInstance ||
            !string.IsNullOrEmpty(target.FailoverPartner) ||
            !new[] { @"localhost\SQLEXPRESS", @"DRAGON\SQLEXPRESS", @".\SQLEXPRESS" }
                .Contains(target.DataSource, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SQL tests require local SQLEXPRESS/VaultFinanceJournalTests with Windows authentication and no attached files/failover.");
        }

        return target.ConnectionString;
    }

    public static string ReadConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables().Build();
        return ValidateConnectionString(configuration.GetConnectionString("SqlTestConnection"));
    }

    public static VaultFinanceDbContext CreateContext(string connectionString) =>
        new(CreateOptions(ValidateConnectionString(connectionString)));

    // Match production's Identity model options when constructing a context outside API DI.
    internal static DbContextOptions<VaultFinanceDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<VaultFinanceDbContext>()
            .UseApplicationServiceProvider(IdentityModelOptions)
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(150)).Options;

    public static async Task EnsureReadyAsync(string connectionString)
    {
        connectionString = ValidateConnectionString(connectionString);
        await InitializationGate.WaitAsync();
        try
        {
            var catalog = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(catalog.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_ID(N'VaultFinanceJournalTests');";
            bool exists = await command.ExecuteScalarAsync() is not DBNull;

            if (!exists)
            {
                if (Environment.GetEnvironmentVariable("VAULTFINANCE_SQL_TESTS_CREATE_DATABASE") != "1")
                {
                    throw new InvalidOperationException(
                        "The dedicated SQL test database does not exist. Explicitly enable its creation; no database was changed.");
                }

                // Fixed identifier, allowlisted server. Never drop, replace, or clear a database.
                command.CommandText = "CREATE DATABASE [VaultFinanceJournalTests];";
                await command.ExecuteNonQueryAsync();
                await using var created = new SqlConnection(connectionString);
                await created.OpenAsync();
                await using var marker = created.CreateCommand();
                marker.CommandText = """
                    EXEC sys.sp_addextendedproperty
                        @name=N'VaultFinance.TestDatabaseOwner',
                        @value=N'VaultFinance.JournalPersistenceTests.v1';
                    """;
                await marker.ExecuteNonQueryAsync();
            }

            await using var target = new SqlConnection(connectionString);
            await target.OpenAsync();
            await using var ownership = target.CreateCommand();
            ownership.CommandText = """
                SELECT CASE WHEN DB_NAME() = N'VaultFinanceJournalTests'
                    AND EXISTS (SELECT 1 FROM sys.extended_properties
                        WHERE class = 0 AND name = N'VaultFinance.TestDatabaseOwner'
                          AND CONVERT(nvarchar(128), value) = N'VaultFinance.JournalPersistenceTests.v1')
                    THEN 1 ELSE 0 END;
                """;
            if (Convert.ToInt32(await ownership.ExecuteScalarAsync()) != 1)
            {
                throw new InvalidOperationException(
                    "The existing SQL test database lacks the project ownership marker. No migration or cleanup was attempted.");
            }

            ownership.CommandText = """
                SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0 AND (s.name <> N'dbo' OR t.name NOT IN (
                    N'Accounts',N'AspNetUsers',N'AspNetRoles',N'AspNetRoleClaims',N'AspNetUserClaims',
                    N'AspNetUserLogins',N'AspNetUserRoles',N'AspNetUserTokens',N'__EFMigrationsHistory',
                    N'FinancialPostingBatches',N'FinancialOperations',N'FinancialOpeningVersions',
                    N'FinancialRequestOutcomes',N'FinancialAuditEntries'));
                """;
            if (Convert.ToInt32(await ownership.ExecuteScalarAsync()) != 0)
            {
                throw new InvalidOperationException("Foreign tables exist in the marked SQL test database. Nothing was migrated or cleaned.");
            }

            await using var context = CreateContext(connectionString);
            var knownMigrations = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if ((await context.Database.GetAppliedMigrationsAsync()).Any(migration => !knownMigrations.Contains(migration)))
            {
                throw new InvalidOperationException("Unknown migration history exists in the SQL test database.");
            }

            // Explicit test opt-in only. API startup never reaches this code.
            await context.Database.MigrateAsync();
        }
        finally { InitializationGate.Release(); }
    }
}
