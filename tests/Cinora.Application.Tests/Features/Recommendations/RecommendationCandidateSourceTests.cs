using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Recommendations;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Milestone 5.1 unit tests for <see cref="RecommendationCandidateSource"/> — the grounded TMDB fan-out that
/// builds the candidate universe (ADR 0017 §1, design §5.3) — against a fully configurable
/// <see cref="RecommendationTmdbClientStub"/> (no network). They prove the pipeline invariants: seen titles are
/// excluded, duplicates across the fan-out collapse, genre ids are resolved to names (unknown ids dropped, the
/// candidate kept), the pool is topped up from popular/top-rated when thin, and the pool is capped.
/// </summary>
public sealed class RecommendationCandidateSourceTests
{
    [Fact]
    public async Task GenerateAsync_excludes_seen_dedups_and_resolves_genre_names()
    {
        var tmdb = new RecommendationTmdbClientStub();
        tmdb.RecommendationsByTitle[(MediaType.Movie, 100)] =
        [
            Summary(300, "Rec A", posterPath: "/a.jpg", genreIds: [28]),
            Summary(301, "Rec B", posterPath: "/b.jpg", genreIds: [18]),
            Summary(100, "The Seed Itself", genreIds: [28]), // seen → must be excluded
        ];
        tmdb.DiscoverByMediaType[MediaType.Movie] =
        [
            Summary(300, "Rec A (again)", genreIds: [28]), // duplicate of 300 → must collapse
            Summary(302, "Disc C", genreIds: [999]),        // 999 is an unknown genre id → dropped, title kept
        ];
        tmdb.GenresByMediaType[MediaType.Movie] =
        [
            new TmdbGenre { TmdbGenreId = 28, Name = "Action" },
            new TmdbGenre { TmdbGenreId = 18, Name = "Drama" },
        ];

        var taste = new UserTasteResult
        {
            Profile = new TasteProfile(),
            SeedKeys = [(100, MediaType.Movie)],
            FavoriteGenreTmdbIds = [28],
            SeenExclusions = new HashSet<(int, MediaType)> { (100, MediaType.Movie), (200, MediaType.Movie) },
            HasSignal = true,
        };

        var source = new RecommendationCandidateSource(tmdb, new StubRecommendationPolicy());

        var candidates = await source.GenerateAsync(taste, CancellationToken.None);

        // 300, 301 (recs) + 302 (discover); the seen seed (100) is excluded and the duplicate 300 collapsed.
        Assert.Equal([300, 301, 302], candidates.Select(candidate => candidate.TmdbId).OrderBy(id => id));
        Assert.DoesNotContain(candidates, candidate => candidate.TmdbId == 100);

        var recA = candidates.Single(candidate => candidate.TmdbId == 300);
        Assert.Equal("Rec A", recA.Title);               // first-seen metadata (from recommendations, not the dup)
        Assert.Equal("/a.jpg", recA.PosterPath);         // poster carried for the card
        Assert.Equal(["Action"], recA.Genres);           // id 28 → "Action"

        var discC = candidates.Single(candidate => candidate.TmdbId == 302);
        Assert.Empty(discC.Genres); // the unknown id 999 was dropped, but the candidate is kept
    }

    [Fact]
    public async Task GenerateAsync_tops_up_from_popular_and_top_rated_when_the_fan_out_is_thin()
    {
        var tmdb = new RecommendationTmdbClientStub();
        tmdb.RecommendationsByTitle[(MediaType.Movie, 100)] = [Summary(300, "Rec A")];
        // No favorite genres → discover returns [] (no unfiltered popularity list).
        tmdb.PopularByMediaType[MediaType.Movie] = [Summary(400, "Popular 1"), Summary(401, "Popular 2")];
        tmdb.TopRatedByMediaType[MediaType.Movie] = [Summary(500, "Top Rated 1")];

        var taste = new UserTasteResult
        {
            Profile = new TasteProfile(),
            SeedKeys = [(100, MediaType.Movie)],
            FavoriteGenreTmdbIds = [], // empty → discover is a no-op
            SeenExclusions = new HashSet<(int, MediaType)> { (100, MediaType.Movie) },
            HasSignal = true,
        };

        var source = new RecommendationCandidateSource(tmdb, new StubRecommendationPolicy { MaxCandidates = 10 });

        var candidates = await source.GenerateAsync(taste, CancellationToken.None);

        // The thin recommendation fan-out (1) is topped up from popular (2) then top-rated (1).
        Assert.Equal([300, 400, 401, 500], candidates.Select(candidate => candidate.TmdbId).OrderBy(id => id));
    }

    [Fact]
    public async Task GenerateAsync_caps_the_pool_to_the_configured_maximum()
    {
        var tmdb = new RecommendationTmdbClientStub();
        tmdb.RecommendationsByTitle[(MediaType.Movie, 100)] =
        [
            Summary(300, "A"), Summary(301, "B"), Summary(302, "C"), Summary(303, "D"), Summary(304, "E"),
        ];

        var taste = new UserTasteResult
        {
            Profile = new TasteProfile(),
            SeedKeys = [(100, MediaType.Movie)],
            FavoriteGenreTmdbIds = [],
            SeenExclusions = new HashSet<(int, MediaType)> { (100, MediaType.Movie) },
            HasSignal = true,
        };

        var source = new RecommendationCandidateSource(tmdb, new StubRecommendationPolicy { MaxCandidates = 3 });

        var candidates = await source.GenerateAsync(taste, CancellationToken.None);

        // Capped to 3, preserving first-seen order (300, 301, 302).
        Assert.Equal([300, 301, 302], candidates.Select(candidate => candidate.TmdbId));
    }

    private static TmdbTitleSummary Summary(
        int tmdbId, string title, string? posterPath = null, IReadOnlyList<int>? genreIds = null) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = posterPath,
            GenreIds = genreIds ?? [],
        };
}
