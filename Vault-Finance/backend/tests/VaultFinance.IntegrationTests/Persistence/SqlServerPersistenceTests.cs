using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence;

namespace VaultFinance.IntegrationTests.Persistence;

public sealed class SqlServerPersistenceTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [SqlServerFact]
    public async Task IdentityAndAccounts_RoundTripWithMultipleCurrenciesAndArchivedState()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = CreateSyntheticUser();
        var created = await manager.CreateAsync(user);
        Assert.True(created.Succeeded);
        Assert.NotNull(await manager.FindByIdAsync(user.Id.ToString()));

        var dop = new Account(Guid.NewGuid(), user.Id, "  Test DOP  ", "dop");
        var usd = new Account(Guid.NewGuid(), user.Id, "Test USD", "USD");
        context.Accounts.AddRange(dop, usd);
        await context.SaveChangesAsync();
        dop.Archive();
        usd.Rename("Renamed test USD");
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var persisted = await context.Accounts.Where(account => account.UserId == user.Id).ToListAsync();

        Assert.Equal(2, persisted.Count);
        var archived = Assert.Single(persisted, account => account.Currency == "DOP");
        var active = Assert.Single(persisted, account => account.Currency == "USD");
        Assert.Equal(dop.Id, archived.Id);
        Assert.Equal("Test DOP", archived.Name);
        Assert.Equal(AccountStatus.Archived, archived.Status);
        Assert.Throws<DomainException>(() => archived.Rename("Forbidden"));
        Assert.Equal(AccountStatus.Active, active.Status);
        Assert.Equal("Renamed test USD", active.Name);
        // Disposal rolls back the entire transaction, including Identity's SaveChanges calls.
    }

    [SqlServerTheory]
    [InlineData("Valid name", "EUR", 1)]
    [InlineData("Valid name", "usd", 1)]
    [InlineData("Valid name", "USD", 99)]
    [InlineData("", "USD", 1)]
    [InlineData(" Leading space", "USD", 1)]
    [InlineData("\tLeading tab", "USD", 1)]
    [InlineData("\u00a0Leading nonbreaking space", "USD", 1)]
    public async Task Database_RejectsInvalidAccountRows(string name, string currency, int status)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var user = CreateSyntheticUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var id = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [Accounts] ([Id], [UserId], [Name], [Currency], [Status]) VALUES ({id}, {user.Id}, {name}, {currency}, {status})"));

        Assert.Equal(547, exception.Number);
    }

    [SqlServerFact]
    public async Task Database_RejectsAccountWithMissingOwner()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Accounts.Add(new Account(Guid.NewGuid(), Guid.NewGuid(), "Test orphan", "DOP"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Equal(547, Assert.IsType<SqlException>(exception.InnerException).Number);
    }

    [SqlServerFact]
    public async Task Database_PreventsCascadingDeletionOfOwnedAccounts()
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var user = CreateSyntheticUser();
        context.Users.Add(user);
        context.Accounts.Add(new Account(Guid.NewGuid(), user.Id, "Test retained", "USD"));
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [AspNetUsers] WHERE [Id] = {user.Id}"));

        Assert.Equal(547, exception.Number);
    }

    [SqlServerTheory]
    [InlineData(nameof(Account.Currency), "USD")]
    [InlineData(nameof(Account.UserId), null)]
    public async Task ChangeTracker_RejectsChangesToPersistedOwnershipOrCurrency(string property, string? value)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultFinanceDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var user = CreateSyntheticUser();
        context.Users.Add(user);
        var account = new Account(Guid.NewGuid(), user.Id, "Test immutable", "DOP");
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        context.Entry(account).Property(property).CurrentValue =
            property == nameof(Account.UserId) ? Guid.NewGuid() : value;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private static ApplicationUser CreateSyntheticUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"test-{Guid.NewGuid():N}@example.invalid"
    };
}
