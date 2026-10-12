using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> b)
    {
        b.ToTable("rules", t =>
        {
            t.HasCheckConstraint("ck_rules_match_type", "match_type IN ('Contains', 'StartsWith', 'Equals')");

            // A rule that does nothing is a mistake, not a rule.
            t.HasCheckConstraint("ck_rules_has_action", "category_id IS NOT NULL OR mark_transfer");
            // btrim would only trim spaces, letting a tab or newline through; \S is any non-whitespace character.
            t.HasCheckConstraint("ck_rules_pattern_not_blank", "pattern ~ '\\S'");
            t.HasCheckConstraint("ck_rules_position_nonnegative", "position >= 0");
        });

        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Pattern).HasMaxLength(200);
        b.Property(r => r.MatchType).HasConversion<string>().HasMaxLength(16);

        // Rules are always read as a user's whole ordered list.
        b.HasIndex(r => new { r.UserId, r.Position, r.Id });

        b.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);

        // A rule that assigns a category goes when that category goes. The composite key means a
        // rule can only point at one of its owner's own categories; with no category (a
        // transfer-only rule) the foreign key is simply not checked.
        b.HasOne<Category>().WithMany()
            .HasForeignKey(r => new { r.UserId, r.CategoryId })
            .HasPrincipalKey(c => new { c.UserId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
