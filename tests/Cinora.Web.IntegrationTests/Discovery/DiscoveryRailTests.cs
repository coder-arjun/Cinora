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
/// Milestone 2.3 mock-first tests for the Home / discovery rails, driven through the real MVC + hand-rolled
/// <see cref="ISender"/> pipeline with a <see cref="FakeTmdbClient"/> replacing the network. They prove the
/// design invariants: GET-only browse (no anti-forgery token needed), the lazy per-rail shell, the
/// card rendering (including the null-poster placeholder and the w342 TMDB URL), CQRS query purity (a rail
/// GET writes nothing), the extended CSP, and the <c>Enum.IsDefined</c> 400 guard.
/// </summary>
/// <remarks>
/// Cinora is login-only (ADR 0023), so every content GET here uses an authenticated client (see
/// <see cref="AuthedAsync"/>). The rail tests replace <see cref="ITmdbClient"/> per test via a
/// <c>WithWebHostBuilder</c>-derived host so the shared factory is never mutated; the static Serilog logger
/// that building a host installs is saved and restored, matching the guard the sibling suites rely on in this
/// non-parallel collection. The shell/guard tests use the shared factory directly (they make no TMDB call).
/// </remarks>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class DiscoveryRailTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public DiscoveryRailTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // T1 — the shell renders three lazy rail containers; no TMDB/DB call is needed (auth is required — login-only).
    [Fact]
    public async Task Index_returns_200_with_three_lazy_rail_shells()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Three lazy rails, each an hx-get to /discover/rail with its kind and media=Movie, triggered on load.
        Assert.Contains("/discover/rail", body, StringComparison.Ordinal);
        Assert.Contains("kind=Trending", body, StringComparison.Ordinal);
        Assert.Contains("kind=Popular", body, StringComparison.Ordinal);
        Assert.Contains("kind=TopRated", body, StringComparison.Ordinal);
        Assert.Contains("media=Movie", body, StringComparison.Ordinal);
        Assert.Contains("hx-trigger=\"load\"", body, StringComparison.Ordinal);

        // Skeleton placeholders are present before the cards stream in.
        Assert.Contains("data-rail-skeleton", body, StringComparison.Ordinal);
    }

    // T2 — a rail partial renders cards (title, details href, w342 poster URL, null-poster placeholder).
    [Fact]
    public async Task Rail_partial_returns_200_with_cards()
    {
        var fake = new FakeTmdbClient
        {
            TrendingMovies =
            [
                Summary(101, "Alpha Title", "/alpha.jpg", vote: 8.4, year: 2021),
                Summary(102, "Beta Title", posterPath: null, vote: 0, year: 2019), // null poster → placeholder
                Summary(103, "Gamma Title", "/gamma.jpg", vote: 7.1, year: 2023),
            ],
        };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/rail?kind=Trending&media=Movie");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Partial only — the layout (full HTML document) is NOT rendered.
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);

        // Each seeded title renders a card with its title and Details href (the fixed 2.5 route pattern).
        Assert.Contains("Alpha Title", body, StringComparison.Ordinal);
        Assert.Contains("Beta Title", body, StringComparison.Ordinal);
        Assert.Contains("Gamma Title", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/101", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/102", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/103", body, StringComparison.Ordinal);

        // The poster card loads directly from TMDB's CDN (https://image.tmdb.org/t/p/w342/...); the null-poster
        // card falls back to the same-origin placeholder (no broken image, no crash).
        Assert.Contains("https://images.weserv.nl/?url=image.tmdb.org/t/p/w342/alpha.jpg", body, StringComparison.Ordinal);
        Assert.Contains("/img/poster-placeholder.svg", body, StringComparison.Ordinal);

        // The tag helper applies the performance hints on every rendered poster.
        Assert.Contains("loading=\"lazy\"", body, StringComparison.Ordinal);
        Assert.Contains("decoding=\"async\"", body, StringComparison.Ordinal);
    }

    // T3 — both discovery responses carry the extended CSP; script-src stays strict (no eval).
    [Fact]
    public async Task Discovery_responses_carry_the_extended_CSP()
    {
        var fake = new FakeTmdbClient
        {
            TrendingMovies = [Summary(201, "Csp Title", "/csp.jpg", vote: 6.0, year: 2020)],
        };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var index = await client.GetAsync("/discover");
        using var rail = await client.GetAsync("/discover/rail?kind=Trending&media=Movie");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rail.StatusCode);

        foreach (var response in new[] { index, rail })
        {
            var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("img-src 'self' https://image.tmdb.org https://images.weserv.nl data:", csp, StringComparison.Ordinal);
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        }
    }

    // T4 — a rail GET is a pure read: the Movies table row count is unchanged (rails never persist, §4.1).
    [Fact]
    public async Task Rail_GET_writes_nothing_to_the_database()
    {
        var fake = new FakeTmdbClient
        {
            TrendingMovies =
            [
                Summary(301, "NoWrite A", "/a.jpg", vote: 6.0, year: 2020),
                Summary(302, "NoWrite B", "/b.jpg", vote: 7.0, year: 2021),
            ],
        };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        var before = await MovieCountAsync(factory);

        using var response = await client.GetAsync("/discover/rail?kind=Trending&media=Movie");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await MovieCountAsync(factory);
        Assert.Equal(before, after);
    }

    // T5 — a bogus kind is a real 400 (the ModelState + Enum.IsDefined guard), not a silently-served default.
    [Fact]
    public async Task Rail_with_a_bogus_kind_returns_400()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/rail?kind=Bogus&media=Movie");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // T6 (regression, /review-ui HIGH) — a rail whose TMDB call throws AFTER the resilience pipeline (an
    // outage) must DEGRADE to the _RailError retry partial with HTTP 200, NOT bubble to a 500. HTMX does not
    // swap a non-2xx response, so a 500 here would leave the skeleton shimmering forever with
    // aria-busy="true"; the 200 error partial lets HTMX swap it and the user retry just this row. The bogus-
    // kind 400 (T5) is untouched — bad client input stays a 400, only a genuine load failure degrades here.
    [Fact]
    public async Task Rail_when_the_TMDB_call_throws_returns_200_with_the_error_retry_partial()
    {
        var fake = new FakeTmdbClient { ThrowOnRails = true };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/rail?kind=Trending&media=Movie");

        // Graceful degradation, not a fault: the error state comes back 200 so HTMX will swap it in.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Partial only (no layout), an on-brand alert whose busy state is cleared (WCAG 4.1.2), and a Retry.
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role=\"alert\"", body, StringComparison.Ordinal);
        Assert.Contains("aria-busy=\"false\"", body, StringComparison.Ordinal);
        Assert.Contains("Retry", body, StringComparison.Ordinal);

        // The Retry control re-fires THIS same rail via attribute-driven HTMX (CSP-safe: no hx-on/inline JS).
        Assert.Contains("hx-get", body, StringComparison.Ordinal);
        Assert.Contains("/discover/rail", body, StringComparison.Ordinal);
        Assert.Contains("kind=Trending", body, StringComparison.Ordinal);
        Assert.Contains("media=Movie", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-on", body, StringComparison.Ordinal);
    }

    // T7 — a rail request with a supported &language= routes to the REGIONAL discover path (titles originally in
    // that language), not the default trending/popular/top-rated. The fake's per-language RegionalRails proves the
    // routing: the language rail returns its own titles while Global (no language) keeps the default path.
    [Fact]
    public async Task Rail_with_a_language_returns_the_regional_titles_for_that_language()
    {
        var fake = new FakeTmdbClient
        {
            TrendingMovies = [Summary(401, "Global Trending", "/g.jpg", vote: 7.0, year: 2022)],
            RegionalRails =
            {
                ["hi"] = [Summary(402, "Hindi Regional", "/h.jpg", vote: 8.0, year: 2023)],
            },
        };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        // With &language=hi the rail shows the Hindi regional title, not the global trending one.
        using var regional = await client.GetAsync("/discover/rail?kind=Trending&media=Movie&language=hi");
        Assert.Equal(HttpStatusCode.OK, regional.StatusCode);
        var regionalBody = await regional.Content.ReadAsStringAsync();
        Assert.Contains("Hindi Regional", regionalBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Global Trending", regionalBody, StringComparison.Ordinal);

        // Global (no language) keeps the default trending path.
        using var global = await client.GetAsync("/discover/rail?kind=Trending&media=Movie");
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
        var globalBody = await global.Content.ReadAsStringAsync();
        Assert.Contains("Global Trending", globalBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Hindi Regional", globalBody, StringComparison.Ordinal);
    }

    private static TmdbTitleSummary Summary(int tmdbId, string title, string? posterPath, double vote, int year) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = posterPath,
            VoteAverage = vote,
            ReleaseDate = new DateOnly(year, 1, 1),
        };

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
            // Force the host to build now so its static-logger side effect is captured and undone below.
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
        TestAuthentication.RegisterAndSignInAsync(factory, "Rail Viewer");

    private static async Task<int> MovieCountAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Movies.AsNoTracking().CountAsync();
    }
}
