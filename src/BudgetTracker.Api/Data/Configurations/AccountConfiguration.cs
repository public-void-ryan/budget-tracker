using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts", t => t.HasCheckConstraint(
            "ck_accounts_type", "type IN ('Checking', 'Savings', 'Credit')"));
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Name).HasMaxLength(100);
        b.Property(a => a.Type).HasConversion<string>().HasMaxLength(16);

        // Target of the composite foreign key from transactions.
        b.HasAlternateKey(a => new { a.UserId, a.Id });
        b.HasIndex(a => new { a.UserId, a.Name }).IsUnique();

        b.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
