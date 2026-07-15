using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Fetches a single title's full detail (hero, metadata, genres, ranked cast) for the Details page
/// (Milestone 2.5) and projects the TMDB read model to a display view model. Returns <c>null</c> when TMDB
/// has no such title (a 404) so the controller can render a 404 rather than an empty page.
/// </summary>
/// <remarks>
/// CQRS-pure: the handler only READS from <see cref="ITmdbClient"/> — it never persists. First-touch
/// persistence of the internal <c>Movie</c> row is a SEPARATE, idempotent <c>EnsureTitleCachedCommand</c>
/// that the controller dispatches AFTER this read (controller orchestration, never handler-to-handler per the
/// cqrs skill); because this query just warmed the cache, that command's <see cref="ITmdbClient"/> read is a
/// cache hit — no extra TMDB round-trip. Caching is transparent (the <c>CachedTmdbClient</c> decorator serves
/// repeat detail reads from the in-memory cache), so this handler stays cache-oblivious.
/// </remarks>
/// <param name="Media">Whether the title is a movie or a series.</param>
/// <param name="TmdbId">The TMDB identifier of the title.</param>
public sealed record GetTitleDetailsQuery(MediaType Media, int TmdbId) : IRequest<TitleDetailsVm?>;

/// <summary>
/// The Details-page display model: the hero (backdrop + poster raw paths), metadata (year, runtime, genre
/// names, TMDB vote as a labeled reference), and a ranked cast summary. Carries only presentation-ready
/// primitives and the <see cref="Domain.Enums.MediaType"/> enum — no TMDB DTOs and no Domain entities.
/// </summary>
public sealed record TitleDetailsVm
{
    /// <summary>The TMDB identifier of the title.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public required MediaType MediaType { get; init; }

    /// <summary>The display title.</summary>
    public required string Title { get; init; }

    /// <summary>The tagline, or <c>null</c> when TMDB provides none.</summary>
    public string? Tagline { get; init; }

    /// <summary>The synopsis, or <c>null</c> when TMDB provides none.</summary>
    public string? Overview { get; init; }

    /// <summary>The RAW TMDB poster path (the view builds a <c>W500</c> URL), or <c>null</c> for the placeholder.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The RAW TMDB backdrop path (the view builds a <c>W1280</c> hero URL), or <c>null</c> for no hero art.</summary>
    public string? BackdropPath { get; init; }

    /// <summary>The release year, or <c>null</c> when TMDB gave no date.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>The runtime in minutes, or <c>null</c> when TMDB provides none.</summary>
    public int? Runtime { get; init; }

    /// <summary>The genre display names.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>The TMDB community vote average on a 0–10 scale (a labeled reference; Cinora reviews arrive Phase 3).</summary>
    public double VoteAverage { get; init; }

    /// <summary>The number of TMDB community votes backing <see cref="VoteAverage"/>.</summary>
    public int VoteCount { get; init; }

    /// <summary>The top-billed cast (ranked, capped), each with a raw <c>W185</c> profile path.</summary>
    public IReadOnlyList<CastMemberVm> Cast { get; init; } = [];

    /// <summary>
    /// Projects a TMDB details read model to the Details view model: collapses the release date to a year,
    /// maps genres to their names, and takes the top-billed cast (by ascending billing order) up to
    /// <paramref name="maxCast"/>. Whitespace-only tagline/character strings are normalized to <c>null</c>.
    /// </summary>
    /// <param name="details">The TMDB details read model to project.</param>
    /// <param name="maxCast">The maximum number of top-billed cast members to include (default 10).</param>
    /// <returns>The Details view model.</returns>
    public static TitleDetailsVm FromDetails(TmdbTitleDetails details, int maxCast = 10)
    {
        ArgumentNullException.ThrowIfNull(details);
        return new TitleDetailsVm
        {
            TmdbId = details.TmdbId,
            MediaType = details.MediaType,
            Title = details.Title,
            Tagline = string.IsNullOrWhiteSpace(details.Tagline) ? null : details.Tagline,
            Overview = string.IsNullOrWhiteSpace(details.Overview) ? null : details.Overview,
            PosterPath = details.PosterPath,
            BackdropPath = details.BackdropPath,
            ReleaseYear = details.ReleaseDate?.Year,
            Runtime = details.Runtime,
            Genres = details.Genres.Select(genre => genre.Name).ToArray(),
            VoteAverage = details.VoteAverage,
            VoteCount = details.VoteCount,
            Cast = details.Cast
                .OrderBy(member => member.Order)
                .Take(maxCast)
                .Select(member => new CastMemberVm
                {
                    TmdbId = member.TmdbId,
                    Name = member.Name,
                    Character = string.IsNullOrWhiteSpace(member.Character) ? null : member.Character,
                    ProfilePath = member.ProfilePath,
                })
                .ToArray(),
        };
    }
}

/// <summary>A single cast member on the Details page: name, role, and a raw <c>W185</c> profile path.</summary>
public sealed record CastMemberVm
{
    /// <summary>The TMDB person identifier.</summary>
    public required int TmdbId { get; init; }

    /// <summary>The actor's name.</summary>
    public required string Name { get; init; }

    /// <summary>The character portrayed, or <c>null</c> when TMDB provides none.</summary>
    public string? Character { get; init; }

    /// <summary>The RAW TMDB profile path (the view builds a <c>W185</c> URL), or <c>null</c> for the placeholder.</summary>
    public string? ProfilePath { get; init; }
}

/// <summary>
/// Handles <see cref="GetTitleDetailsQuery"/> by fetching the title's details from <see cref="ITmdbClient"/>
/// and projecting them to a <see cref="TitleDetailsVm"/>; a TMDB 404 (a <c>null</c> details response) maps to
/// a <c>null</c> result so the controller can 404.
/// </summary>
/// <param name="tmdb">The TMDB read port — the handler's ONLY dependency (no DB, no mediator).</param>
public sealed class GetTitleDetailsQueryHandler(ITmdbClient tmdb)
    : IRequestHandler<GetTitleDetailsQuery, TitleDetailsVm?>
{
    /// <inheritdoc />
    public async Task<TitleDetailsVm?> Handle(GetTitleDetailsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var details = await tmdb.GetDetailsAsync(request.Media, request.TmdbId, cancellationToken);
        return details is null ? null : TitleDetailsVm.FromDetails(details);
    }
}
