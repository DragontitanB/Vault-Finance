using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Domain.Accounts;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence.Configurations;

namespace VaultFinance.Infrastructure.Persistence;

public sealed class VaultFinanceDbContext(DbContextOptions<VaultFinanceDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new AccountConfiguration());
    }
}
