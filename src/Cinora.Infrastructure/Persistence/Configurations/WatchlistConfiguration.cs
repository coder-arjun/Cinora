using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Watchlist"/> entry entity.</summary>
internal sealed class WatchlistConfiguration : IEntityTypeConfiguration<Watchlist>
{
    public void Configure(EntityTypeBuilder<Watchlist> builder)
    {
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        // WHY: at most one watchlist entry per user per title.
        builder.HasIndex(w => new { w.UserId, w.MovieId }).IsUnique();

        // Milestone 4.4 (§10) — the watchlist-page keyset (recently-added). Serves the "All" view's
        // (UserId, AddedAtUtc DESC, Id DESC) keyset; the clustered Id is appended implicitly so the tie-break is
        // index-supported. Perf-only — reads are correct with or without it.
        builder.HasIndex(w => new { w.UserId, w.AddedAtUtc })
               .HasDatabaseName("IX_Watchlists_UserId_AddedAtUtc");

        // Milestone 4.4 (§10) — the status-filtered page keyset AND the per-status grouped counts (by the
        // (UserId, Status) prefix). Perf-only.
        builder.HasIndex(w => new { w.UserId, w.Status, w.AddedAtUtc })
               .HasDatabaseName("IX_Watchlists_UserId_Status_AddedAtUtc");

        builder.Property(w => w.Status).IsRequired();
        builder.Property(w => w.AddedAtUtc).IsRequired();

        // Restrict — deleting a user must not silently erase their watchlist.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(w => w.UserId)
               .OnDelete(DeleteBehavior.Restrict);

        // Restrict — a cached title cannot be deleted while watchlist entries reference it.
        builder.HasOne<Movie>()
               .WithMany()
               .HasForeignKey(w => w.MovieId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
