using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.ToTable("transactions", t =>
        {
            t.HasCheckConstraint("ck_transactions_amount_nonzero", "amount <> 0");
            t.HasCheckConstraint(
                "ck_transactions_import_pair", "(import_hash IS NULL) = (import_seq IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.ImportHash).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasIndex(x => new { x.AccountId, x.PostedOn }).IsDescending(false, true);

        // The cross-account "newest first" list: keyset pagination walks this index in order,
        // so a page costs the same however deep into the history it is.
        b.HasIndex(x => new { x.UserId, x.PostedOn, x.Id }).IsDescending(false, true, true);

        // Re-importing a file is rejected; identical rows within one file are told
        // apart by ImportSeq. Nulls are distinct in Postgres, so manual entries
        // are unconstrained.
        b.HasIndex(x => new { x.AccountId, x.ImportHash, x.ImportSeq }).IsUnique();

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Account>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.AccountId })
            .HasPrincipalKey(a => new { a.UserId, a.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The database behavior is ON DELETE SET NULL (category_id), set in the
        // migration with raw SQL: EF can only emit SET NULL for every column of a
        // composite key, which would null the NOT NULL user_id. NoAction here keeps
        // EF from trying to null both columns client-side.
        b.HasOne<Category>().WithMany()
            .HasForeignKey(x => new { x.UserId, x.CategoryId })
            .HasPrincipalKey(c => new { c.UserId, c.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}
