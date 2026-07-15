using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Watchlist;

/// <summary>
/// The model for the reusable <c>_WatchlistControl</c> partial (§5.3) — the single status control rendered on
/// Details, the discovery rails and search cards. It addresses the title by its TMDB coordinates
/// (<see cref="TmdbId"/> + <see cref="Media"/>, which the <c>WatchlistController</c> resolves to the internal
/// <c>MovieId</c> via <c>EnsureTitleCachedCommand</c> — ADR 0008/0014) plus the current user's
/// <see cref="Status"/> (<c>null</c> = not in list). No user id is carried — the acting user is resolved
/// server-side (ADR 0009). Anonymous callers (<see cref="IsAuthenticated"/> false) render a sign-in affordance
/// instead of a write.
/// </summary>
/// <param name="TmdbId">The title's TMDB identifier.</param>
/// <param name="Media">Whether the title is a movie or a series.</param>
/// <param name="Status">The current user's status for the title, or <c>null</c> when it is not in their list.</param>
/// <param name="IsAuthenticated">Whether the viewer is signed in (drives write control vs sign-in affordance).</param>
public sealed record WatchlistControlVm(int TmdbId, MediaType Media, WatchlistStatus? Status, bool IsAuthenticated)
{
    /// <summary>
    /// Whether the control renders on the Details page. When <c>true</c> the Watched-state review nudge (§5.4)
    /// links the in-page lazy <c>#my-review</c> write region; when <c>false</c> (a rail/search card) it links
    /// the title's Details reviews section instead. Round-trips through the write forms so the re-rendered
    /// control keeps the right nudge target.
    /// </summary>
    public bool OnDetails { get; init; }
}
