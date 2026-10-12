using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BudgetTracker.Api.Data.Configurations;

public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions", t =>
        {
            t.HasCheckConstraint("ck_sessions_token_hash_length", "octet_length(token_hash) = 32");
            t.HasCheckConstraint("ck_sessions_expires_after_created", "expires_at > created_at");
        });
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();

        // Every authenticated request looks a session up by token hash.
        b.HasIndex(s => s.TokenHash).IsUnique();

        // Evicting a user's oldest session when they exceed the cap.
        b.HasIndex(s => new { s.UserId, s.CreatedAt });

        // The periodic purge of expired sessions.
        b.HasIndex(s => s.ExpiresAt);

        b.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
