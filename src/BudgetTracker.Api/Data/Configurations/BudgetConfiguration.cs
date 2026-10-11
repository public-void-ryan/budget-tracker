using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> b)
    {
        b.ToTable("budgets", t =>
        {
            t.HasCheckConstraint("ck_budgets_amount_nonnegative", "amount >= 0");
            t.HasCheckConstraint("ck_budgets_month_first_day", "EXTRACT(DAY FROM month) = 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasPrecision(19, 4);

        b.HasIndex(x => new { x.UserId, x.CategoryId, x.Month }).IsUnique();

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Category>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.CategoryId })
            .HasPrincipalKey(c => new { c.UserId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
