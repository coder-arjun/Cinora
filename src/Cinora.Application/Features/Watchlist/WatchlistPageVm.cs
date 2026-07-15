using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Watchlist;

/// <summary>
/// The sort order of the watchlist page (Milestone 4.4, §6.1). The surface is deliberately small and
/// <b>index-backed</b>: <see cref="RecentlyAdded"/> is a keyset over <c>(AddedAtUtc DESC, Id DESC)</c> served
/// directly by the §10 composite indexes (<c>(UserId, AddedAtUtc)</c> and <c>(UserId, Status, AddedAtUtc)</c>)
/// with no sort/scan step. A "recently updated" (over <c>UpdatedAtUtc</c>) or a "title A–Z" sort is deferred:
/// the former needs its own index (or an accepted per-user scan) and the latter is a cross-table keyset — both
/// are follow-ups (§6.1, §13), not added here where the constraint is index-backed only.
/// </summary>
public enum WatchlistSort
{
    /// <summary>Newest-added first — the default keyset over <c>(AddedAtUtc DESC, Id DESC)</c>.</summary>
    RecentlyAdded = 0,
}

/// <summary>
/// An opaque keyset cursor into the watchlist page, ordered newest-added-first by
/// <c>(AddedAtUtc DESC, Id DESC)</c>. A page returns the cursor of its last item; the next request fetches
/// strictly-older rows. Never a page number and never an <c>OFFSET</c> (deep-pagination cost — §12, ADR 0014),
/// mirroring <c>FeedCursor</c>.
/// </summary>
/// <param name="SortKey">The <see cref="WatchlistItemVm.AddedAtUtc"/> of the last item on the current page.</param>
/// <param name="Id">The <see cref="WatchlistItemVm.EntryId"/> of the last item on the current page (the tie-breaker).</param>
public sealed record WatchlistCursor(DateTime SortKey, Guid Id);

/// <summary>
/// One entry on the current user's watchlist page, projected for display. It carries the tracked title's TMDB
/// coordinates and poster (so the card links to Details and shows a thumbnail, and the reused
/// <c>_WatchlistControl</c> can address the title), the current user's <see cref="Status"/>, and the timestamps
/// that drive the keyset ordering. Presentation-ready primitives only — no Domain entity and no EF type — so it
/// is safe to hand straight to a Razor partial. <see cref="EntryId"/> is the <c>Watchlist</c> row id, carried
/// as the keyset tie-breaker (exactly as <c>FeedItemVm.ReviewId</c> is), not shown to the user.
/// </summary>
public sealed record WatchlistItemVm
{
    /// <summary>The <c>Watchlist</c> entry id — the keyset tie-breaker (not rendered).</summary>
    public required Guid EntryId { get; init; }

    /// <summary>The internal <c>Movie.Id</c> of the tracked title.</summary>
    public required Guid MovieId { get; init; }

    /// <summary>The title's TMDB identifier (for the Details link and the watchlist control).</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series (drives the Details route segment and the control).</summary>
    public required MediaType Media { get; init; }

    /// <summary>The title's display title.</summary>
    public required string Title { get; init; }

    /// <summary>The title's raw TMDB poster path, or <c>null</c> (the view renders the placeholder).</summary>
    public string? PosterPath { get; init; }

    /// <summary>The title's release year, or <c>null</c> when TMDB gave no release date.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>The current user's viewing status for the title.</summary>
    public required WatchlistStatus Status { get; init; }

    /// <summary>The UTC instant the entry was added (also the keyset sort key).</summary>
    public required DateTime AddedAtUtc { get; init; }

    /// <summary>The UTC instant the status last changed, or <c>null</c> if it has not changed since being added.</summary>
    public DateTime? UpdatedAtUtc { get; init; }
}

/// <summary>One keyset page of the current user's watchlist.</summary>
/// <param name="Items">The watchlist items on this page, in the requested sort order.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or <c>null</c> when there is no next page.</param>
/// <param name="HasMore">Whether a further page exists.</param>
public sealed record WatchlistPageVm(
    IReadOnlyList<WatchlistItemVm> Items,
    WatchlistCursor? NextCursor,
    bool HasMore);
