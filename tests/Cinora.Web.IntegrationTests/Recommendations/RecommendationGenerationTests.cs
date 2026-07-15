using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Recommendations;

/// <summary>
/// Milestone 5.1 end-to-end test for the grounded generate → rank → guard pipeline, driven through the real
/// Application <see cref="ISender"/> + the real <see cref="UserTasteProfileBuilder"/> and
/// <see cref="RecommendationCandidateSource"/> over the migrated <c>CinoraTest</c> LocalDB. Only the two external
/// boundaries are faked: a <see cref="FakeTmdbClient"/> (the recommendation/discover fan-out) and a
/// <see cref="FakeRecommendationEngine"/> (the LLM, which echoes candidates plus one hallucination). It proves
/// the correctness core: every served pick is grounded in the offered candidate set, the model's off-list
/// hallucination is dropped by the guard, and titles the user has already seen never appear. TMDB ids sit in the
/// 972_xxx range so the shared database stays isolated from the sibling suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class RecommendationGenerationTests : IAsyncLifetime
{
    private const int SeenReviewedTmdbId = 972_001;
    private const int SeenWatchlistTmdbId = 972_002;
    private const int RecommendedATmdbId = 972_010;
    private const int RecommendedBTmdbId = 972_011;
    private const int DiscoveredTmdbId = 972_020;
    private const int HallucinatedTmdbId = 972_999; // never inserted — only ever a RankedPick the guard must drop
    private const int FavoriteGenreTmdbId = 972_028;

    private readonly CinoraWebApplicationFactory _factory;

    public RecommendationGenerationTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GenerateRecommendations_returns_only_grounded_picks_dropping_hallucinations_and_seen_titles()
    {
        var userId = await RegisterServerUserAsync("Rachel Recommend 5.1");
        await SeedTasteAsync(userId);

        // The fan-out returns two recommendations (one is the SEEN reviewed title, which must be excluded) and
        // two discover results (one is the SEEN watchlist title, which must be excluded).
        var fakeTmdb = new FakeTmdbClient();
        fakeTmdb.Recommendations[(MediaType.Movie, SeenReviewedTmdbId)] =
        [
            Summary(RecommendedATmdbId, "Recommended A"),
            Summary(RecommendedBTmdbId, "Recommended B"),
            Summary(SeenReviewedTmdbId, "Seen Reviewed (echoed)"),
        ];
        fakeTmdb.DiscoverByGenre[MediaType.Movie] =
        [
            Summary(DiscoveredTmdbId, "Discovered C"),
            Summary(SeenWatchlistTmdbId, "Seen Watchlist (echoed)"),
        ];

        // The engine echoes the offered candidates (all grounded) and appends one off-list hallucination.
        var fakeEngine = new FakeRecommendationEngine
        {
            ExtraPicks =
            [
                new RankedPick { TmdbId = HallucinatedTmdbId, Media = MediaType.Movie, Reason = "A title that does not exist." },
            ],
        };

        using var factory = CreateFactoryWith(fakeTmdb, fakeEngine);

        var set = await SendAsync(factory, new GenerateRecommendationsCommand(userId));

        // A grounded, non-empty AI set.
        Assert.Equal(RecommendationSource.Ai, set.Source);
        Assert.NotEmpty(set.Picks);

        var pickedIds = set.Picks.Select(pick => pick.TmdbId).ToHashSet();

        // Every pick is grounded: exactly the three unseen candidates the fan-out produced (no more, no fewer).
        // The engine returned these three PLUS the hallucination; keeping only three proves the guard dropped it.
        Assert.Equal([RecommendedATmdbId, RecommendedBTmdbId, DiscoveredTmdbId], pickedIds.OrderBy(id => id));

        // The guard dropped the model's off-list hallucination (it was returned by the engine but is not a candidate).
        Assert.DoesNotContain(HallucinatedTmdbId, pickedIds);

        // Titles the user has already reviewed or watchlisted are never recommended.
        Assert.DoesNotContain(SeenReviewedTmdbId, pickedIds);
        Assert.DoesNotContain(SeenWatchlistTmdbId, pickedIds);

        // Card metadata comes from the grounded candidate (the fan-out title), not the model.
        Assert.Equal("Recommended A", set.Picks.Single(pick => pick.TmdbId == RecommendedATmdbId).Title);
        Assert.Equal("Discovered C", set.Picks.Single(pick => pick.TmdbId == DiscoveredTmdbId).Title);

        Assert.Equal(1, fakeEngine.CallCount);
    }

    // Seeds the user's taste: one reviewed movie (linked to a favorite genre → seeds recs + discover) and one
    // watchlisted movie; both are "seen" and must be excluded from the candidates.
    private async Task SeedTasteAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var genre = Genre.Create(FavoriteGenreTmdbId, "Test Action 970");
        db.Genres.Add(genre);

        var reviewed = Movie.FromTmdb(
            SeenReviewedTmdbId, MediaType.Movie, "Seen Reviewed 970", null, new DateTime(2014, 11, 7), "/r.jpg", null);
        db.Movies.Add(reviewed);
        db.MovieGenres.Add(MovieGenre.Link(reviewed.Id, genre.Id));
        db.Reviews.Add(Review.Create(userId, reviewed.Id, Rating.From(9), "A grounded seed review."));

        var watchlisted = Movie.FromTmdb(
            SeenWatchlistTmdbId, MediaType.Movie, "Seen Watchlist 970", null, new DateTime(2021, 1, 1), "/w.jpg", null);
        db.Movies.Add(watchlisted);
        db.Watchlists.Add(Watchlist.Add(userId, watchlisted.Id, WatchlistStatus.PlanToWatch));

        await db.SaveChangesAsync();
    }

    // Creates a user (ApplicationUser + domain User) through the atomic registration seam; the review's Restrict
    // FK to User needs a real row.
    private async Task<Guid> RegisterServerUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"recs-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }

    // Builds a host with BOTH external boundaries faked (TMDB + the engine), leaving the shared factory untouched
    // and restoring the process-global Serilog logger the host build installs (the sibling-suite guard).
    private WebApplicationFactory<Program> CreateFactoryWith(ITmdbClient fakeTmdb, IRecommendationEngine fakeEngine)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fakeTmdb);
                services.RemoveAll<IRecommendationEngine>();
                services.AddScoped(_ => fakeEngine);
            }));

        try
        {
            _ = factory.Services;
        }
        finally
        {
            Log.Logger = originalLogger;
        }

        return factory;
    }

    private static async Task<TResponse> SendAsync<TResponse>(
        WebApplicationFactory<Program> factory, IRequest<TResponse> request)
    {
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(request);
    }

    private static TmdbTitleSummary Summary(int tmdbId, string title) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = "/candidate.jpg",
            ReleaseDate = new DateOnly(2022, 1, 1),
        };
}
