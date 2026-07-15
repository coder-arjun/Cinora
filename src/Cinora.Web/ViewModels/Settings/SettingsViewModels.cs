namespace Cinora.Web.ViewModels.Settings;

/// <summary>
/// The form posted to <c>POST /settings/profile</c> to edit the CURRENT user's own display name and privacy.
/// It binds NO user id — the acting user is resolved server-side (ADR 0009), so a spoofed id field would simply
/// be unbound and ignored. The property names mirror <c>UpdateProfileCommand</c> so the settings form's
/// <c>asp-for</c> fields bind straight through.
/// </summary>
public sealed class UpdateProfileForm
{
    /// <summary>The new display name (validated by <c>UpdateProfileCommandValidator</c>).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The new visibility: <c>true</c> = public, <c>false</c> = friends-only (§4.3).</summary>
    public bool IsProfilePublic { get; set; }

    /// <summary>The chosen default movie language (ISO-639-1 code, or empty for Global); server-validated.</summary>
    public string? DefaultLanguage { get; set; }
}

/// <summary>
/// The view model for the settings page (<c>GET /settings/profile</c>) and the re-rendered profile form fragment.
/// Carries the current display name + privacy (to pre-fill the form), the avatar key (for the <c>_Avatar</c>
/// seam), a client-side upload size hint from <c>FileStorageOptions</c> (the server stays authoritative), and a
/// <see cref="Saved"/> flag the HTMX form response sets to show a "saved" confirmation. The field names
/// (<see cref="DisplayName"/>, <see cref="IsProfilePublic"/>) match <see cref="UpdateProfileForm"/> so the same
/// model backs the <c>_ProfileForm</c> partial's bound inputs.
/// </summary>
public sealed class ProfileSettingsViewModel
{
    /// <summary>The current display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether the profile is currently public (drives the privacy radios).</summary>
    public required bool IsProfilePublic { get; init; }

    /// <summary>The current avatar storage key, or <c>null</c> when none is set (monogram).</summary>
    public string? AvatarFileKey { get; init; }

    /// <summary>The current default movie language (ISO-639-1 code), or <c>null</c> for Global (drives the select).</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>The maximum upload size, in bytes — a client-side hint only; the server is authoritative.</summary>
    public required long MaxUploadBytes { get; init; }

    /// <summary>Whether this render is the post-save confirmation (shows a "saved" note in the fragment).</summary>
    public bool Saved { get; init; }
}

/// <summary>
/// The form posted to <c>POST /settings/notifications</c> to edit the CURRENT user's per-type Web Push
/// preferences (Milestone 6.3). Binds NO user id — the acting user is resolved server-side (ADR 0009). The
/// property names mirror <c>UpdateNotificationPreferencesCommand</c> so the checkbox fields bind straight through
/// (each rendered with the tag helper's hidden-companion, so an unchecked box binds <c>false</c>, not "missing").
/// </summary>
public sealed class UpdateNotificationPreferencesForm
{
    /// <summary>Whether friend-request events should push to this user's devices.</summary>
    public bool PushFriendRequests { get; set; }

    /// <summary>Whether friend-accepted events should push to this user's devices.</summary>
    public bool PushFriendAccepted { get; set; }

    /// <summary>Whether review-like events should push to this user's devices.</summary>
    public bool PushReviewLikes { get; set; }

    /// <summary>Whether comment events should push to this user's devices.</summary>
    public bool PushComments { get; set; }
}

/// <summary>
/// The view model for the notification-settings page (<c>GET /settings/notifications</c>) and the re-rendered
/// toggles fragment. Carries the four current per-type push flags (to drive the checkboxes) and a
/// <see cref="Saved"/> flag the HTMX form response sets to show a "saved" confirmation. The field names match
/// <see cref="UpdateNotificationPreferencesForm"/> so the same model backs the <c>_NotificationPreferencesForm</c>
/// partial's bound inputs.
/// </summary>
public sealed class NotificationSettingsViewModel
{
    /// <summary>Whether friend-request events currently push.</summary>
    public required bool PushFriendRequests { get; init; }

    /// <summary>Whether friend-accepted events currently push.</summary>
    public required bool PushFriendAccepted { get; init; }

    /// <summary>Whether review-like events currently push.</summary>
    public required bool PushReviewLikes { get; init; }

    /// <summary>Whether comment events currently push.</summary>
    public required bool PushComments { get; init; }

    /// <summary>Whether this render is the post-save confirmation (shows a "saved" note in the fragment).</summary>
    public bool Saved { get; init; }
}
