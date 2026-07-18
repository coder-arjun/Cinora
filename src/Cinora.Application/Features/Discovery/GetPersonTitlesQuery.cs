using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Fetches a cast member (actor) and their most notable titles — the query behind the "click a cast member
/// to see their famous films" page (feature #9). Reads TMDB via <see cref="ITmdbClient"/> only (CQRS-pure:
/// no DB, no <c>SaveChanges</c>), then ranks the person's acting credits to the ones fans would recognise.
/// </summary>
/// <param name="PersonId">The TMDB person identifier (from a cast member's card on the Details page).</param>
public sealed record GetPersonTitlesQuery(int PersonId) : IRequest<PersonTitlesVm?>;

/// <summary>A cast member and their ranked, display-ready titles.</summary>
/// <param name="PersonId">The TMDB person id.</param>
/// <param name="Name">The person's display name.</param>
/// <param name="ProfilePath">The raw TMDB profile image path, or <c>null</c>.</param>
/// <param name="Titles">The person's most notable titles as poster cards, best first.</param>
public sealed record PersonTitlesVm(int PersonId, string Name, string? ProfilePath, IReadOnlyList<TitleCardVm> Titles);

/// <summary>
/// Handles <see cref="GetPersonTitlesQuery"/>: fetches the person's combined acting credits, dedupes them,
/// keeps only titles with a poster, and ranks the "famous" ones (those with enough community votes) by rating
/// so a fan sees the star's recognisable films first — falling back to all credits when too few are famous.
/// </summary>
/// <param name="tmdb">The TMDB read port — the handler's ONLY dependency.</param>
public sealed class GetPersonTitlesQueryHandler(ITmdbClient tmdb)
    : IRequestHandler<GetPersonTitlesQuery, PersonTitlesVm?>
{
    // The page shows a focused grid, not an exhaustive filmography — cap it.
    private const int MaxTitles = 24;

    // A "famous" title has at least this many TMDB votes: enough that ranking by rating surfaces well-known
    // films rather than an obscure short with a single 10/10 vote.
    private const int FameVoteFloor = 100;

    // Only rank purely by rating when enough titles clear the fame floor; otherwise a lesser-known actor's
    // page would collapse to nothing. Below this, fall back to the actor's full (posterful) credit list.
    private const int MinFamousForFameRanking = 5;

    /// <inheritdoc />
    public async Task<PersonTitlesVm?> Handle(GetPersonTitlesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credits = await tmdb.GetPersonCreditsAsync(request.PersonId, cancellationToken);
        if (credits is null)
        {
            return null; // TMDB has no such person (404) → the controller renders a 404.
        }

        // A person can be credited on the same title more than once (multiple roles); keep one card per title.
        var distinct = credits.Titles
            .Where(title => title.PosterPath is not null)
            .GroupBy(title => (title.TmdbId, title.MediaType))
            .Select(group => group.First())
            .ToList();

        var famous = distinct.Where(title => title.VoteCount >= FameVoteFloor).ToList();
        var pool = famous.Count >= MinFamousForFameRanking ? famous : distinct;

        var cards = pool
            .OrderByDescending(title => title.VoteAverage)
            .ThenByDescending(title => title.VoteCount)
            .Take(MaxTitles)
            .Select(TitleCardVm.FromSummary)
            .ToArray();

        return new PersonTitlesVm(credits.PersonId, credits.Name, credits.ProfilePath, cards);
    }
}
