using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VaultFinance.Domain.Accounts;
using VaultFinance.Infrastructure.Identity;

namespace VaultFinance.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        // Match String.Trim's whitespace set without restricting internal whitespace.
        var whitespace = string.Join(" + ", new[]
        {
            9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195,
            8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288
        }.Select(code => $"NCHAR({code})"));

        builder.ToTable("Accounts", table =>
        {
            table.HasCheckConstraint("CK_Accounts_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Accounts_UserId", "[UserId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Accounts_Name",
                $"DATALENGTH([Name]) BETWEEN 2 AND 200 AND DATALENGTH([Name]) = DATALENGTH(TRIM({whitespace} FROM [Name]))");
            table.HasCheckConstraint("CK_Accounts_Currency",
                "[Currency] COLLATE Latin1_General_100_BIN2 IN ('DOP', 'USD')");
            table.HasCheckConstraint("CK_Accounts_Status", "[Status] IN (1, 2)");
        });

        builder.HasKey(account => account.Id);
        builder.Property(account => account.Id).ValueGeneratedNever();

        // Explicit mapping lets EF bind all four constructor parameters, including get-only properties.
        builder.Property(account => account.UserId).IsRequired();
        builder.Property(account => account.Name).HasMaxLength(Account.MaximumNameLength).IsRequired();
        builder.Property(account => account.Currency).HasColumnType("varchar(3)").IsRequired();
        builder.Property(account => account.Status).HasConversion<int>().IsRequired();

        builder.Property(account => account.UserId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(account => account.Currency).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(account => account.UserId);
        builder.HasIndex(account => new { account.UserId, account.Status });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(account => account.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
