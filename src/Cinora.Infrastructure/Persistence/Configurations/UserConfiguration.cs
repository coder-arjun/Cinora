using Cinora.Domain.Entities;
using Cinora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the domain <see cref="User"/> aggregate and its shared-PK 1:1 with the Identity principal.</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);

        // WHY: the PK equals ApplicationUser.Id (created by UserManager); EF must never generate it.
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.DisplayName)
               .IsRequired()
               .HasMaxLength(User.DisplayNameMaxLength);

        // Upper-invariant normalization of DisplayName, enforced UNIQUE so it can serve as a case-insensitive
        // login handle (users sign in with display name OR email). Kept in lockstep by User.Create/Rename.
        builder.Property(u => u.NormalizedDisplayName)
               .IsRequired()
               .HasMaxLength(User.DisplayNameMaxLength);
        builder.HasIndex(u => u.NormalizedDisplayName).IsUnique();

        // Preferred movie language (lower-case ISO-639-1) for the Discover default; null = Global (no filter).
        builder.Property(u => u.DefaultLanguage)
               .HasMaxLength(User.DefaultLanguageMaxLength);

        // Storage key for the avatar; bounded so it is never nvarchar(max).
        builder.Property(u => u.AvatarFileKey)
               .HasMaxLength(512);

        builder.Property(u => u.IsProfilePublic).IsRequired();

        // Notification preferences (Milestone 6.3, ADR 0020 §4) as an EF OWNED type: four bit NOT NULL columns on
        // the Users table itself — no new table, no join. HasDefaultValue(true) yields "bit NOT NULL DEFAULT 1",
        // so the additive migration backfills EVERY existing user to opt-in (default-on). SAFETY on the INSERT-omit
        // trap: HasDefaultValue marks a property store-generated-on-add, so EF would omit a CLR-default (false)
        // value on INSERT — but User.Create always sets Preferences all-true (a non-default value), so all four
        // columns are always written explicitly on insert; updates always send the changed value. The default
        // therefore only ever serves the ADD-COLUMN backfill of existing rows.
        builder.OwnsOne(u => u.Preferences, preferences =>
        {
            preferences.Property(p => p.PushFriendRequests)
                       .HasColumnName("PushFriendRequests").IsRequired().HasDefaultValue(true);
            preferences.Property(p => p.PushFriendAccepted)
                       .HasColumnName("PushFriendAccepted").IsRequired().HasDefaultValue(true);
            preferences.Property(p => p.PushReviewLikes)
                       .HasColumnName("PushReviewLikes").IsRequired().HasDefaultValue(true);
            preferences.Property(p => p.PushComments)
                       .HasColumnName("PushComments").IsRequired().HasDefaultValue(true);
        });

        builder.Property(u => u.CreatedAtUtc).IsRequired();

        // Shared-PK 1:1 with the Identity principal (ADR 0003). Configured from the Infrastructure
        // side because Domain cannot see ApplicationUser. Restrict so neither row silently deletes the
        // other and no cascade path is introduced through the Users table.
        builder.HasOne<ApplicationUser>()
               .WithOne()
               .HasForeignKey<User>(u => u.Id)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
