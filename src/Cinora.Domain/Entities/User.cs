using Cinora.Domain.Common;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Entities;

/// <summary>
/// The pure profile/social user aggregate. It carries display and visibility state only —
/// authentication data (credentials, logins, security stamp) lives on the Infrastructure
/// <c>ApplicationUser</c>, which shares this same <see cref="Id"/> (ADR 0003). Every other domain
/// entity references a user by this <see cref="Guid"/> identifier, never by an Identity type.
/// </summary>
public sealed class User
{
    /// <summary>The maximum allowed length of a <see cref="DisplayName"/>.</summary>
    public const int DisplayNameMaxLength = 100;

    /// <summary>The maximum length of a <see cref="DefaultLanguage"/> code (ISO-639-1, with room for a region tag).</summary>
    public const int DefaultLanguageMaxLength = 8;

    private User()
    {
    }

    /// <summary>The user's unique identifier, shared with the Identity principal.</summary>
    public Guid Id { get; private set; }

    /// <summary>The publicly shown name for the user.</summary>
    public string DisplayName { get; private set; } = null!;

    /// <summary>
    /// The upper-invariant normalization of <see cref="DisplayName"/>, enforced by a unique index so it can
    /// serve as a case-insensitive login handle (users sign in with display name OR email). Kept in lockstep
    /// with <see cref="DisplayName"/> by <see cref="Create"/>/<see cref="Rename"/>.
    /// </summary>
    public string NormalizedDisplayName { get; private set; } = null!;

    /// <summary>
    /// The user's preferred movie language as a lower-case ISO-639-1 code (e.g. <c>"hi"</c>, <c>"ko"</c>), or
    /// <c>null</c> for Global (no original-language filter). Used as the Discover default when set.
    /// </summary>
    public string? DefaultLanguage { get; private set; }

    /// <summary>The storage key of the user's avatar image, or <c>null</c> when none is set.</summary>
    public string? AvatarFileKey { get; private set; }

    /// <summary>Whether the user's profile is visible to everyone.</summary>
    public bool IsProfilePublic { get; private set; }

    /// <summary>
    /// The user's per-type Web Push opt-out preferences (Milestone 6.3). Defaults to all-enabled and is mapped as
    /// an EF owned type (columns on the <c>Users</c> table, no join). Governs push fan-out only; the in-app inbox
    /// row always persists.
    /// </summary>
    public NotificationPreferences Preferences { get; private set; } = NotificationPreferences.Default;

    /// <summary>The UTC instant the profile was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Creates a new user profile.</summary>
    /// <param name="id">The identifier shared with the Identity principal (created in the same registration transaction).</param>
    /// <param name="displayName">The public display name; required, max <see cref="DisplayNameMaxLength"/> characters.</param>
    /// <returns>A fully initialized, publicly visible <see cref="User"/>.</returns>
    public static User Create(Guid id, string displayName)
    {
        var name = Guard.Required(displayName, DisplayNameMaxLength, nameof(displayName));
        return new()
        {
            Id = id,
            DisplayName = name,
            NormalizedDisplayName = Normalize(name),
            IsProfilePublic = true,
            Preferences = NotificationPreferences.Default,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Changes the user's display name, keeping <see cref="NormalizedDisplayName"/> in lockstep.</summary>
    /// <param name="displayName">The new display name; required, max <see cref="DisplayNameMaxLength"/> characters.</param>
    public void Rename(string displayName)
    {
        var name = Guard.Required(displayName, DisplayNameMaxLength, nameof(displayName));
        DisplayName = name;
        NormalizedDisplayName = Normalize(name);
    }

    /// <summary>Sets or clears the user's preferred movie language.</summary>
    /// <param name="languageCode">A lower-cased ISO-639-1 code, or <c>null</c>/blank for Global (no filter).</param>
    public void SetDefaultLanguage(string? languageCode) =>
        DefaultLanguage = string.IsNullOrWhiteSpace(languageCode) ? null : languageCode.Trim().ToLowerInvariant();

    /// <summary>The upper-invariant normalization used for the unique, case-insensitive display-name login handle.</summary>
    /// <param name="displayName">The display name to normalize.</param>
    /// <returns>The trimmed, upper-invariant form.</returns>
    public static string Normalize(string displayName) => displayName.Trim().ToUpperInvariant();

    /// <summary>Sets or clears the user's avatar storage key.</summary>
    /// <param name="avatarFileKey">The new avatar storage key, or <c>null</c> to clear it.</param>
    public void SetAvatar(string? avatarFileKey) => AvatarFileKey = avatarFileKey;

    /// <summary>Sets whether the profile is publicly visible.</summary>
    /// <param name="isProfilePublic">The new visibility.</param>
    public void SetProfileVisibility(bool isProfilePublic) => IsProfilePublic = isProfilePublic;

    /// <summary>Replaces the user's Web Push notification preferences (Milestone 6.3).</summary>
    /// <param name="preferences">The new preferences; required.</param>
    public void UpdateNotificationPreferences(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Preferences = preferences;
    }
}
