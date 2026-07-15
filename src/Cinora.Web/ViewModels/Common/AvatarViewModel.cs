namespace Cinora.Web.ViewModels.Common;

/// <summary>
/// The render size for the shared <c>_Avatar</c> seam. Kept small and preset (rather than free-form classes) so
/// every surface renders a consistent avatar; the frontend-agent can extend the presets when elevating the UI.
/// </summary>
public enum AvatarSize
{
    /// <summary>Small — inline with review/comment/feed rows.</summary>
    Small = 0,

    /// <summary>Medium — friend cards and list rows.</summary>
    Medium = 1,

    /// <summary>Large — the profile / settings header.</summary>
    Large = 2,
}

/// <summary>
/// The model for the shared <c>_Avatar</c> partial (§3, §11): everything the seam needs to render a real avatar
/// <c>&lt;img&gt;</c> (via <c>IFileStorage.GetUrl</c>) or the monogram fallback is just the owner's
/// <see cref="DisplayName"/> and <see cref="AvatarFileKey"/>. Designed so any surface that shows a user (profile,
/// review card, feed item, comment) can drop the partial in with only those two values plus a size. The partial
/// — never the caller — decides whether the key is safe to sign into a URL, so a corrupt key degrades to the
/// monogram rather than throwing (ADR 0013 §2.4; architecture L3).
/// </summary>
/// <param name="DisplayName">The user's display name (drives the monogram initial and the image alt text).</param>
/// <param name="AvatarFileKey">The user's avatar storage key, or <c>null</c> to render the monogram.</param>
/// <param name="Size">The render size preset; defaults to <see cref="AvatarSize.Medium"/>.</param>
/// <param name="Decorative">
/// When <c>true</c>, the avatar is rendered as a DECORATIVE image (empty <c>alt</c> / <c>aria-hidden</c> monogram)
/// so a screen reader does not double-announce the name — use this on every surface where a VISIBLE adjacent name
/// (a review/comment/feed/friend card's name link, the profile header, the notification message) already conveys
/// the user. Defaults to <c>false</c> (the accessibility-safe default: a MEANINGFUL <c>alt</c> / labelled
/// monogram), which is correct wherever the avatar stands alone with no adjacent visible name — e.g. the nav chip.
/// </param>
/// <param name="Eager">
/// When <c>true</c>, the avatar <c>&lt;img&gt;</c> loads with <c>loading="eager"</c> instead of the default
/// <c>lazy</c> (Milestone 6.4 §6.5, backlog 4.2 Low). Use it for ABOVE-THE-FOLD placements — the nav chip and the
/// profile/settings header — so the image is not deferred and does not paint late; the reserved box means there is
/// no CLS either way. Defaults to <c>false</c> (lazy) for everything below the fold (cards, lists, feed rows).
/// </param>
public sealed record AvatarViewModel(
    string DisplayName,
    string? AvatarFileKey,
    AvatarSize Size = AvatarSize.Medium,
    bool Decorative = false,
    bool Eager = false);
