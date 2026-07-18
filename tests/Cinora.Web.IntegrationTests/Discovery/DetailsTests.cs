using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Discovery;

/// <summary>
/// Milestone 2.5 mock-first tests for the title Details page, driven through the real MVC + hand-rolled
/// <see cref="ISender"/> pipeline with a <see cref="FakeTmdbClient"/> replacing the network, against the
/// migrated <c>CinoraTest</c> LocalDB. They prove: the Details render (hero/metadata/cast), the
/// first-touch persistence side-effect (exactly one <c>Movie</c> row, idempotent), the TMDB-404 → HTTP 404,
/// the unknown-media-segment → 404 guard, and the extended CSP. Cinora is login-only (ADR 0023), so every
/// content GET uses an authenticated client (see <see cref="AuthedAsync"/>); a TMDB id outside the seeded set
/// keeps the shared database isolated.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class DetailsTests : IAsyncLifetime
{
    private const int MovieTmdbId = 900_800;

    private readonly CinoraWebApplicationFactory _factory;

    public DetailsTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // D1 — the Details page renders the title, overview, genres, and cast for an authenticated GET.
    [Fact]
    public async Task Details_returns_200_with_title_overview_genres_and_cast()
    {
        var fake = FakeWithMovie();
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync($"/discover/title/movie/{MovieTmdbId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase); // full page (layout)
        Assert.Contains("The Detail Title", body, StringComparison.Ordinal);
        Assert.Contains("A tagline worth reading.", body, StringComparison.Ordinal); // tagline (non-null branch)
        Assert.Contains("A canned detail overview.", body, StringComparison.Ordinal);
        Assert.Contains("2022", body, StringComparison.Ordinal);          // release year in the metadata line
        Assert.Contains("7.8", body, StringComparison.Ordinal);           // labeled TMDB vote (VoteCount>0 branch)
        Assert.Contains("Action", body, StringComparison.Ordinal);        // a genre name
        Assert.Contains("Drama", body, StringComparison.Ordinal);
        Assert.Contains("Jane Lead", body, StringComparison.Ordinal);     // a cast name
        Assert.Contains("John Support", body, StringComparison.Ordinal);
        // Backdrop/poster/profile images load directly from TMDB's CDN (https://image.tmdb.org/t/p/{size}/{file}) —
        // the browser fetches them, with no server-side proxy dependency.
        Assert.Contains("https://images.weserv.nl/?url=image.tmdb.org/t/p/", body, StringComparison.Ordinal);
    }

    // D2 — a Details GET first-touch persists exactly one Movie row and is idempotent on repeat (ADR 0007).
    [Fact]
    public async Task Details_get_persists_the_movie_once_and_is_idempotent()
    {
        var fake = FakeWithMovie();
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var first = await client.GetAsync($"/discover/title/movie/{MovieTmdbId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, await MovieCountAsync(factory, MovieTmdbId)); // exactly one row after the first touch

        using var second = await client.GetAsync($"/discover/title/movie/{MovieTmdbId}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await MovieCountAsync(factory, MovieTmdbId)); // still one — the upsert is idempotent
    }

    // D3 — a title TMDB has no record of (a 404) returns HTTP 404 and persists nothing.
    [Fact]
    public async Task Details_for_an_unknown_title_returns_404_and_persists_nothing()
    {
        const int unknownId = 900_801;
        var fake = new FakeTmdbClient(); // no Details entry → GetDetailsAsync returns null (models a 404)
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync($"/discover/title/movie/{unknownId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await MovieCountAsync(factory, unknownId)); // no row created for a 404
    }

    // D4 — an unknown media segment is a non-existent title page → 404 (guarded before any dispatch).
    [Fact]
    public async Task Details_with_a_bogus_media_segment_returns_404()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/title/bogus/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // D5 — the Details response carries the extended CSP (img-src TMDB CDN; script-src stays strict).
    [Fact]
    public async Task Details_response_carries_the_extended_CSP()
    {
        var fake = FakeWithMovie();
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync($"/discover/title/movie/{MovieTmdbId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("img-src 'self' https://image.tmdb.org https://images.weserv.nl data:", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
    }

    // D6 — each cast member on Details links to their person page (/discover/person/{id}); feature #9 click-through.
    [Fact]
    public async Task Details_cast_members_link_to_the_person_page()
    {
        var fake = FakeWithMovie();
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync($"/discover/title/movie/{MovieTmdbId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/discover/person/1", body, StringComparison.Ordinal); // Jane Lead
        Assert.Contains("/discover/person/2", body, StringComparison.Ordinal); // John Support
    }

    // D7 — the person page renders the actor and their top-rated titles (feature #9: cast → their films),
    // mixing movies + series, each linking to its own media route.
    [Fact]
    public async Task Person_page_returns_the_actor_and_their_top_titles()
    {
        var fake = new FakeTmdbClient();
        fake.People[6193] = new TmdbPersonCredits
        {
            PersonId = 6193,
            Name = "Leo Star",
            ProfilePath = "/leo.jpg",
            Titles =
            [
                new TmdbTitleSummary { TmdbId = 27205, MediaType = MediaType.Movie, Title = "Inception", PosterPath = "/incep.jpg", VoteAverage = 8.4, VoteCount = 30000, ReleaseDate = new DateOnly(2010, 7, 16) },
                new TmdbTitleSummary { TmdbId = 1396, MediaType = MediaType.Series, Title = "Breaking Something", PosterPath = "/bb.jpg", VoteAverage = 8.9, VoteCount = 12000, ReleaseDate = new DateOnly(2008, 1, 20) },
            ],
        };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/person/6193");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Leo Star", body, StringComparison.Ordinal);           // the actor's name
        Assert.Contains("Inception", body, StringComparison.Ordinal);          // a movie credit
        Assert.Contains("/discover/title/movie/27205", body, StringComparison.Ordinal);
        Assert.Contains("Breaking Something", body, StringComparison.Ordinal); // a series credit
        Assert.Contains("/discover/title/series/1396", body, StringComparison.Ordinal);
    }

    private static FakeTmdbClient FakeWithMovie()
    {
        var fake = new FakeTmdbClient();
        fake.Details[(MediaType.Movie, MovieTmdbId)] = new TmdbTitleDetails
        {
            TmdbId = MovieTmdbId,
            MediaType = MediaType.Movie,
            Title = "The Detail Title",
            Tagline = "A tagline worth reading.",
            Overview = "A canned detail overview.",
            PosterPath = "/detailposter.jpg",
            BackdropPath = "/detailbackdrop.jpg",
            ReleaseDate = new DateOnly(2022, 6, 1),
            Runtime = 128,
            VoteAverage = 7.8,
            VoteCount = 250,
            Genres =
            [
                new TmdbGenre { TmdbGenreId = 28, Name = "Action" }, // both seeded → no genre upsert needed
                new TmdbGenre { TmdbGenreId = 18, Name = "Drama" },
            ],
            Cast =
            [
                new TmdbCastMember { TmdbId = 1, Name = "Jane Lead", Character = "The Hero", ProfilePath = "/jane.jpg", Order = 0 },
                new TmdbCastMember { TmdbId = 2, Name = "John Support", Character = "The Foil", ProfilePath = null, Order = 1 },
            ],
        };
        return fake;
    }

    // Builds a fake-backed derived host (leaving the shared factory untouched) and restores the process-global
    // Serilog logger that building the host installs, so the sibling non-parallel suites keep the base logger.
    private WebApplicationFactory<Program> CreateFactoryWith(ITmdbClient fake)
    {
        var originalLogger = Log.Logger;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITmdbClient>();
                services.AddScoped(_ => fake);
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

    // Cinora is login-only (ADR 0023): every content GET needs an authenticated client, so tests register a
    // throwaway viewer on the SAME factory instance that will issue the request (cookies are per-host).
    private static Task<AuthenticatedTestUser> AuthedAsync(WebApplicationFactory<Program> factory) =>
        TestAuthentication.RegisterAndSignInAsync(factory, "Details Viewer");

    private static async Task<int> MovieCountAsync(WebApplicationFactory<Program> factory, int tmdbId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Movies
            .AsNoTracking()
            .CountAsync(movie => movie.TmdbId == tmdbId && movie.MediaType == MediaType.Movie);
    }
}
