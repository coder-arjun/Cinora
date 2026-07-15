using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Watchlist;

/// <summary>
/// The form posted to <c>POST /watchlist</c> to set (add/update) a title's watchlist status from any surface.
/// It carries the title's TMDB coordinates (the controller resolves them to the internal <c>MovieId</c> via
/// <c>EnsureTitleCachedCommand</c> — ADR 0008/0014) plus the desired status. It deliberately carries NO user
/// id: the acting user is resolved server-side (ADR 0009), so a spoofed <c>UserId</c> field is simply unbound
/// and ignored (test W5). <see cref="OnDetails"/> is a presentation hint that round-trips so the re-rendered
/// control shows the right Watched review nudge (§5.4).
/// </summary>
public sealed class SetWatchlistForm
{
    /// <summary>The TMDB identifier of the title.</summary>
    public int TmdbId { get; set; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public MediaType Media { get; set; }

    /// <summary>The viewing status to set (validated by <c>SetWatchlistStatusCommandValidator</c>).</summary>
    public WatchlistStatus Status { get; set; }

    /// <summary>Whether the control is rendered on the Details page (drives the Watched review nudge target).</summary>
    public bool OnDetails { get; set; }
}
