using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", t => t.HasCheckConstraint("ck_users_email_lowercase", "email = lower(email)"));
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedNever();
        b.Property(u => u.Email).HasMaxLength(320);
        b.HasIndex(u => u.Email).IsUnique();
        b.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
    }
}
