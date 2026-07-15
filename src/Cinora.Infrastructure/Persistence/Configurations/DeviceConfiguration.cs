using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Device"/> Web Push subscription entity.</summary>
internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        // Push endpoints are URLs and can be long; bounded so it is never nvarchar(max). Deliberately
        // NOT indexed directly — an nvarchar(2048) index key would exceed SQL Server's 900-byte limit; the
        // fixed-width EndpointHash below is the indexed dedup key instead.
        builder.Property(d => d.Endpoint)
               .IsRequired()
               .HasMaxLength(2048);

        // The SHA-256 hex digest of Endpoint (Milestone 6.3) — fixed-width char(64), so it IS indexable. ASCII
        // hex, so non-unicode fixed length keeps the composite index key small (16 + 64 bytes < 900).
        builder.Property(d => d.EndpointHash)
               .IsRequired()
               .IsUnicode(false)
               .IsFixedLength()
               .HasMaxLength(64);

        // Base64url-encoded push encryption material — comfortably within these bounds.
        builder.Property(d => d.P256dhKey).IsRequired().HasMaxLength(256);
        builder.Property(d => d.AuthSecret).IsRequired().HasMaxLength(256);

        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Property(d => d.LastSeenUtc).IsRequired();

        // WHY: a user's registered devices (device lookups on the send path).
        builder.HasIndex(d => d.UserId);

        // WHY: race-safe subscribe upsert — at most one Device per (user, endpoint). A concurrent duplicate
        // subscribe now trips this unique index instead of inserting a second row (ADR 0020 §3.3).
        builder.HasIndex(d => new { d.UserId, d.EndpointHash }).IsUnique();

        // Restrict — deleting a user must not cascade into push subscriptions unexpectedly.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(d => d.UserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
