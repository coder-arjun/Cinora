using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Fetches a single Home-feed rail (Trending / Popular / Top-Rated) for a media type and projects it to a
/// list of display cards. This is the one real query behind the lazy per-rail Home shell (Milestone 2.3):
/// the shell ships static skeletons and each rail streams in via its own HTMX call, so one query per rail
/// keeps the three TMDB round-trips independent and independently cacheable.
/// </summary>
/// <remarks>
/// CQRS-pure: the handler only READS from <see cref="ITmdbClient"/> — it never touches
/// <c>IAppDbContext</c>, never calls <c>SaveChangesAsync</c>, and never dispatches another request. Rails do
/// NOT persist (only the Details first-touch path writes a <c>Movie</c> row — ADR 0007). Genre chips are
/// deferred to the Details page in Phase 2, so the query stays <see cref="ITmdbClient"/>-only (no genre
/// id→name map). Caching is transparent — the registered <c>CachedTmdbClient</c> decorator serves repeat
/// calls from the in-memory cache, so this handler stays cache-oblivious.
/// </remarks>
/// <param name="Kind">Which curated rail to fetch.</param>
/// <param name="Media">Whether to fetch movies or series.</param>
/// <param name="Language">The original-language filter (lower-case ISO-639-1); <c>null</c>/empty = Global (no filter).</param>
public sealed record GetTitleRailQuery(RailKind Kind, MediaType Media, string? Language = null) : IRequest<RailVm>;

/// <summary>One rendered rail: its kind, its media type, and the ordered cards to display.</summary>
/// <param name="Kind">The rail this view model represents.</param>
/// <param name="Media">The media type the rail was fetched for.</param>
/// <param name="Items">The rail's cards, in TMDB's returned order; empty when TMDB returned nothing.</param>
public sealed record RailVm(RailKind Kind, MediaType Media, IReadOnlyList<TitleCardVm> Items);

/// <summary>
/// A single poster card in a rail or search result. Carries the RAW TMDB poster path (not a full URL — the
/// view builds the URL and picks a size via the image tag-helper) and no Domain entities or TMDB DTOs, so
/// it is safe to hand straight to a Razor partial.
/// </summary>
public sealed record TitleCardVm
{
    /// <summary>The TMDB identifier, used to build the card's Details link.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series (drives the Details route segment).</summary>
    public required MediaType MediaType { get; init; }

    /// <summary>The display title.</summary>
    public required string Title { get; init; }

    /// <summary>The release year (from the release date), or <c>null</c> when TMDB gave no date.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>The RAW TMDB poster path (e.g. <c>/abc.jpg</c>), or <c>null</c> to render the placeholder.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The TMDB community vote average on a 0–10 scale; the card may show it as a subtle badge or hide it.</summary>
    public double VoteAverage { get; init; }

    /// <summary>
    /// Projects a TMDB list summary to a display card: keeps the raw poster path (the view builds the URL
    /// and picks a size via the image tag-helper) and collapses the release date to just the year (the card
    /// shows a year, not a full date). This is the single projection shared by BOTH the rail handler and the
    /// search handler (DRY) so the mapping cannot drift in two places.
    /// </summary>
    /// <param name="summary">The TMDB list summary to project.</param>
    /// <returns>A display card carrying the raw poster path and release year.</returns>
    public static TitleCardVm FromSummary(TmdbTitleSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new TitleCardVm
        {
            TmdbId = summary.TmdbId,
            MediaType = summary.MediaType,
            Title = summary.Title,
            ReleaseYear = summary.ReleaseDate?.Year,
            PosterPath = summary.PosterPath,
            VoteAverage = summary.VoteAverage,
        };
    }
}

/// <summary>
/// Handles <see cref="GetTitleRailQuery"/> by calling the matching <see cref="ITmdbClient"/> rail method for
/// the requested kind and mapping each returned <see cref="TmdbTitleSummary"/> to a <see cref="TitleCardVm"/>.
/// </summary>
/// <param name="tmdb">The TMDB read port — the handler's ONLY dependency (no DB, no mediator).</param>
public sealed class GetTitleRailQueryHandler(ITmdbClient tmdb)
    : IRequestHandler<GetTitleRailQuery, RailVm>
{
    /// <inheritdoc />
    public async Task<RailVm> Handle(GetTitleRailQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A specific language routes through the regional discover path (titles ORIGINALLY in that language);
        // Global (null/empty) keeps the trending/popular/top-rated behaviour. The default arm is unreachable in
        // practice — the controller's Enum.IsDefined guard rejects any undefined kind before the query is sent —
        // but it keeps the switch exhaustive (and defends the handler if it is ever reused).
        IReadOnlyList<TmdbTitleSummary> summaries;
        if (string.IsNullOrWhiteSpace(request.Language))
        {
            summaries = request.Kind switch
            {
                RailKind.Trending => await tmdb.GetTrendingAsync(request.Media, cancellationToken),
                RailKind.Popular => await tmdb.GetPopularAsync(request.Media, cancellationToken),
                RailKind.TopRated => await tmdb.GetTopRatedAsync(request.Media, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Kind,
                    "Unknown rail kind; the controller's Enum.IsDefined guard should have rejected it."),
            };
        }
        else
        {
            summaries = await tmdb.GetRegionalRailAsync(request.Media, request.Kind, request.Language, cancellationToken);
        }

        // Shared projection (TitleCardVm.FromSummary) — the same factory the search handler calls (DRY).
        var cards = summaries.Select(TitleCardVm.FromSummary).ToArray();
        return new RailVm(request.Kind, request.Media, cards);
    }
}
