using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Domain.Accounts;
using VaultFinance.Infrastructure.Identity;
using VaultFinance.Infrastructure.Persistence.Configurations;
using VaultFinance.Infrastructure.Persistence.FinancialJournal;

namespace VaultFinance.Infrastructure.Persistence;

public sealed class VaultFinanceDbContext(DbContextOptions<VaultFinanceDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FinancialPostingBatchRecord> FinancialPostingBatches => Set<FinancialPostingBatchRecord>();
    public DbSet<FinancialOperationRecord> FinancialOperations => Set<FinancialOperationRecord>();
    public DbSet<FinancialOpeningVersionRecord> FinancialOpeningVersions => Set<FinancialOpeningVersionRecord>();
    public DbSet<FinancialRequestOutcomeRecord> FinancialRequestOutcomes => Set<FinancialRequestOutcomeRecord>();
    public DbSet<FinancialAuditEntryRecord> FinancialAuditEntries => Set<FinancialAuditEntryRecord>();
    public DbSet<FinancialAccountMovement> FinancialAccountMovements => Set<FinancialAccountMovement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new AccountConfiguration());
        FinancialJournalConfiguration.Configure(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateJournalChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateJournalChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateJournalChanges()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not (FinancialPostingBatchRecord or FinancialOperationRecord or
                FinancialOpeningVersionRecord or FinancialRequestOutcomeRecord or FinancialAuditEntryRecord))
            {
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Financial journal records are append-only.");
            }

            // Reject precision/range loss before SQL's decimal parameter conversion can round it.
            if (entry.State == EntityState.Added && entry.Entity is FinancialOperationRecord operation)
            {
                _ = operation.ReadAmount();
            }
            if (entry.State == EntityState.Added && entry.Entity is FinancialRequestOutcomeRecord outcome)
            {
                outcome.ValidateForStorage();
            }
        }
    }
}
