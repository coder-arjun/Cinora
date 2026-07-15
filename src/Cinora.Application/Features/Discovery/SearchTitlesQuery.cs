using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Searches TMDB for titles matching a free-text query and projects the current page to display cards
/// (Milestone 2.4). This is the phase's first request backed by a real FluentValidation validator
/// (<see cref="SearchTitlesQueryValidator"/>): an empty/over-long query is rejected by the wired
/// <c>ValidationBehavior</c> before this handler ever runs.
/// </summary>
/// <remarks>
/// CQRS-pure: the handler only READS from <see cref="ITmdbClient"/> — it never touches
/// <c>IAppDbContext</c>, never calls <c>SaveChangesAsync</c>, and never dispatches another request. Search
/// does NOT persist (only the Details first-touch path writes a <c>Movie</c> row — ADR 0007). Caching is
/// transparent: <see cref="ITmdbClient.SearchAsync"/> is already wrapped by the <c>CachedTmdbClient</c>
/// decorator (15-min TTL, trim+lower normalized key), so this handler stays cache-oblivious.
/// </remarks>
/// <param name="Query">The free-text search term (validated non-empty, 1–100 chars).</param>
/// <param name="Media">Whether to search movies or series (defaults to <see cref="MediaType.Movie"/>).</param>
/// <param name="Page">The 1-based TMDB result page to fetch (defaults to 1).</param>
public sealed record SearchTitlesQuery(string Query, MediaType Media = MediaType.Movie, int Page = 1)
    : IRequest<SearchResultsVm>;

/// <summary>
/// One page of search results: the projected cards plus the paging metadata the view needs to render the
/// "N results" summary and drive the load-more sentinel toward <see cref="TotalPages"/>.
/// </summary>
public sealed record SearchResultsVm
{
    /// <summary>The cards on this page, in TMDB's returned order; empty when the query matched nothing.</summary>
    public IReadOnlyList<TitleCardVm> Items { get; init; } = [];

    /// <summary>The query echoed back — builds the load-more sentinel URL and the results summary line.</summary>
    public required string Query { get; init; }

    /// <summary>The media type echoed back — builds the load-more sentinel URL.</summary>
    public required MediaType Media { get; init; }

    /// <summary>The 1-based index of this page.</summary>
    public required int Page { get; init; }

    /// <summary>The total number of pages TMDB reports for the query.</summary>
    public required int TotalPages { get; init; }

    /// <summary>The total number of results TMDB reports across all pages.</summary>
    public required int TotalResults { get; init; }

    /// <summary>
    /// Whether a further page exists. TMDB rejects <c>page &gt; 500</c>, so the load-more chain is capped at
    /// that hard maximum regardless of the reported <see cref="TotalPages"/>.
    /// </summary>
    public required bool HasMore { get; init; }
}

/// <summary>
/// Handles <see cref="SearchTitlesQuery"/> by calling <see cref="ITmdbClient.SearchAsync"/> and projecting
/// each returned summary to a <see cref="TitleCardVm"/> via the shared <see cref="TitleCardVm.FromSummary"/>
/// factory (the same projection the rail handler uses).
/// </summary>
/// <param name="tmdb">The TMDB read port — the handler's ONLY dependency (no DB, no mediator).</param>
public sealed class SearchTitlesQueryHandler(ITmdbClient tmdb)
    : IRequestHandler<SearchTitlesQuery, SearchResultsVm>
{
    // TMDB rejects page numbers above 500, so the load-more chain must stop there even if TotalPages is larger.
    private const int TmdbMaxPage = 500;

    /// <inheritdoc />
    public async Task<SearchResultsVm> Handle(SearchTitlesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await tmdb.SearchAsync(request.Media, request.Query, request.Page, cancellationToken);

        var items = page.Items.Select(TitleCardVm.FromSummary).ToArray();
        return new SearchResultsVm
        {
            Items = items,
            Query = request.Query,
            Media = request.Media,
            Page = page.Page,
            TotalPages = page.TotalPages,
            TotalResults = page.TotalResults,
            HasMore = page.Page < Math.Min(page.TotalPages, TmdbMaxPage),
        };
    }
}
