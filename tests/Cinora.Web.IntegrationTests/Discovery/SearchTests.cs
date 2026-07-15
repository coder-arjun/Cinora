using System.Net;
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Discovery;

/// <summary>
/// Milestone 2.4 mock-first tests for Search, driven through the real MVC + hand-rolled <see cref="ISender"/>
/// pipeline with a <see cref="FakeTmdbClient"/> replacing the network. They prove the design invariants: the
/// GET-only search page + prompt (S1), the validator wired into the pipeline (S2), the results
/// partial with reused cards (S3), the too-short guard returning a 200 prompt not a 400 (S4), the
/// self-replacing load-more sentinel with a clean terminal state (S5), the 200 error/retry partial on a
/// post-resilience TMDB outage (S6), and the strict CSP being unchanged by the @alpinejs/csp switch (S7).
/// </summary>
/// <remarks>
/// Cinora is login-only (ADR 0023), so every content GET here uses an authenticated client (see
/// <see cref="AuthedAsync"/>). Like the sibling rail suite, the tests that need TMDB data replace
/// <see cref="ITmdbClient"/> per test via a <c>WithWebHostBuilder</c>-derived host (leaving the shared factory
/// untouched) and save/restore the static Serilog logger that building a host installs. The page/guard/
/// validator tests use the shared factory directly (they make no TMDB call).
/// </remarks>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class SearchTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public SearchTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // S1 — the full Search page renders (authenticated — Cinora is login-only, ADR 0023) with the Alpine input,
    // the live region, and the prompt.
    [Fact]
    public async Task Search_page_returns_200_with_the_input_and_prompt()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The registered-by-name Alpine component and the HTMX-swapped live region (announced politely).
        Assert.Contains("x-data=\"search\"", body, StringComparison.Ordinal);
        Assert.Contains("id=\"search-results\"", body, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", body, StringComparison.Ordinal);

        // No query yet → the calm idle prompt, not results.
        Assert.Contains("Search for a movie", body, StringComparison.Ordinal);
    }

    // S2 — the FIRST real validator is wired into the pipeline: a blank query fails BEFORE the handler runs.
    [Fact]
    public async Task Blank_query_dispatch_yields_validation_error_with_a_Query_key()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // A DIRECT dispatch (the browser paths guard to the prompt, so this is the only route to the 400): the
        // ValidationBehavior throws the Application ValidationException, which the GlobalExceptionHandler maps
        // to a 400 ValidationProblemDetails over HTTP. The failure is keyed by the offending property.
        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => sender.Send(new SearchTitlesQuery(string.Empty, MediaType.Movie, 1)));

        Assert.Contains(nameof(SearchTitlesQuery.Query), exception.Errors.Keys);
    }

    // S3 — a valid query returns a partial (no layout) of reused poster cards with the results summary.
    [Fact]
    public async Task Results_partial_returns_200_cards_for_a_valid_query()
    {
        var fake = new FakeTmdbClient
        {
            SearchTotalPages = 1,
            SearchTotalResults = 3,
        };
        fake.SearchPages[1] =
        [
            Summary(501, "Alpha Title", "/alpha.jpg", vote: 8.4, year: 2021),
            Summary(502, "Beta Title", posterPath: null, vote: 0, year: 2019), // null poster → placeholder
            Summary(503, "Gamma Title", "/gamma.jpg", vote: 7.1, year: 2023),
        ];
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=dune&media=Movie");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Partial only — the full HTML document (layout) is NOT rendered.
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);

        // Each seeded title renders a card with its title and the fixed Details href pattern.
        Assert.Contains("Alpha Title", body, StringComparison.Ordinal);
        Assert.Contains("Beta Title", body, StringComparison.Ordinal);
        Assert.Contains("Gamma Title", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/501", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/502", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/503", body, StringComparison.Ordinal);

        // Reused _TitleCard: the poster loads directly from TMDB (https://image.tmdb.org/t/p/w342/...); null → placeholder.
        Assert.Contains("https://images.weserv.nl/?url=image.tmdb.org/t/p/w342/alpha.jpg", body, StringComparison.Ordinal);
        Assert.Contains("/img/poster-placeholder.svg", body, StringComparison.Ordinal);

        // The results summary echoes the query.
        Assert.Contains("result", body, StringComparison.Ordinal);
        Assert.Contains("dune", body, StringComparison.Ordinal);
    }

    // S4 — a too-short query is the idle state, not an error: 200 prompt (guarded BEFORE dispatch), never 400.
    [Fact]
    public async Task Too_short_query_returns_200_prompt_not_400()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=a&media=Movie");

        // 200 so HTMX swaps it (a non-2xx would leave the region spinning — the 2.3 lesson).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Search for a movie", body, StringComparison.Ordinal);
    }

    // S5 — the self-replacing sentinel advances page and stops cleanly at the last page (terminal, no re-arm).
    [Fact]
    public async Task Load_more_advances_the_page_and_appends_then_stops()
    {
        var fake = new FakeTmdbClient
        {
            SearchTotalPages = 2,
            SearchTotalResults = 40,
        };
        fake.SearchPages[1] = [Summary(601, "Page One Title", "/p1.jpg", vote: 7.0, year: 2020)];
        fake.SearchPages[2] = [Summary(602, "Page Two Title", "/p2.jpg", vote: 6.5, year: 2018)];
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        // Page 1 (HasMore) arms a sentinel that GETs page 2 when revealed.
        using var page1 = await client.GetAsync("/discover/search/results?q=dune&media=Movie&page=1");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var page1Body = await page1.Content.ReadAsStringAsync();
        Assert.Contains("Page One Title", page1Body, StringComparison.Ordinal);
        Assert.Contains("/discover/search/results", page1Body, StringComparison.Ordinal);
        Assert.Contains("page=2", page1Body, StringComparison.Ordinal);
        Assert.Contains("hx-trigger=\"revealed\"", page1Body, StringComparison.Ordinal);

        // Page 2 is the last page (HasMore false) → cards + a terminal marker, and NO further sentinel.
        using var page2 = await client.GetAsync("/discover/search/results?q=dune&media=Movie&page=2");
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        var page2Body = await page2.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", page2Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Page Two Title", page2Body, StringComparison.Ordinal);
        Assert.DoesNotContain("page=3", page2Body, StringComparison.Ordinal);      // the chain stops
        Assert.Contains("reached the end", page2Body, StringComparison.OrdinalIgnoreCase);
    }

    // S6 — a post-resilience TMDB failure degrades to the 200 error/retry partial, NOT a 500 (HTMX won't swap).
    [Fact]
    public async Task Failing_search_returns_200_error_retry_partial()
    {
        var fake = new FakeTmdbClient { ThrowOnSearch = true };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=dune&media=Movie");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Partial only (no layout), an on-brand alert whose busy state is cleared (WCAG 4.1.2), and a Retry.
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("role=\"alert\"", body, StringComparison.Ordinal);
        Assert.Contains("aria-busy=\"false\"", body, StringComparison.Ordinal);
        Assert.Contains("Retry", body, StringComparison.Ordinal);

        // The Retry re-fires the SAME results GET (q/media echoed) via attribute-driven HTMX — CSP-safe.
        Assert.Contains("hx-get", body, StringComparison.Ordinal);
        Assert.Contains("/discover/search/results", body, StringComparison.Ordinal);
        Assert.Contains("q=dune", body, StringComparison.Ordinal);
        Assert.Contains("media=Movie", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-on", body, StringComparison.Ordinal);
    }

    // S7 — the @alpinejs/csp switch keeps the CSP strict: script-src 'self', no eval, img-src unchanged.
    [Fact]
    public async Task Search_responses_keep_the_strict_CSP()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' https://image.tmdb.org https://images.weserv.nl data:", csp, StringComparison.Ordinal);
    }

    // S8 — a bogus media enum is a real 400 on both actions (the ModelState + Enum.IsDefined guard §11.6).
    [Fact]
    public async Task Search_and_results_with_a_bogus_media_return_400()
    {
        using var viewer = await AuthedAsync(_factory);
        var client = viewer.Client;

        using var page = await client.GetAsync("/discover/search?q=dune&media=Bogus");
        using var results = await client.GetAsync("/discover/search/results?q=dune&media=Bogus");

        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, results.StatusCode);
    }

    // S9 — the full Search page server-renders results inline for a valid query (deep-link / no-JS baseline),
    // and seeds the term (data-query) so the Alpine init() keeps it under JS.
    [Fact]
    public async Task Search_page_with_a_valid_query_server_renders_results_inline()
    {
        var fake = new FakeTmdbClient { SearchTotalPages = 1, SearchTotalResults = 1 };
        fake.SearchPages[1] = [Summary(701, "Deep Link Title", "/dl.jpg", vote: 7.0, year: 2022)];
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search?q=dune&media=Movie");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Full page (layout rendered) with the page-1 results server-rendered inline.
        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deep Link Title", body, StringComparison.Ordinal);
        Assert.Contains("/discover/title/movie/701", body, StringComparison.Ordinal);

        // The deep-linked term is seeded for the no-JS baseline + the Alpine init() (fixes the review HIGH).
        Assert.Contains("data-query=\"dune\"", body, StringComparison.Ordinal);
    }

    // S10 — a valid query that matches nothing is a calm 200 "No matches" state with no load-more sentinel.
    [Fact]
    public async Task Valid_query_with_no_results_returns_200_no_matches_state()
    {
        var fake = new FakeTmdbClient { SearchTotalPages = 1, SearchTotalResults = 0 };
        // SearchPages[1] left empty (default) → a search miss.
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=zznothing&media=Movie");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No matches", body, StringComparison.Ordinal);
        Assert.Contains("zznothing", body, StringComparison.Ordinal);                 // query echoed
        Assert.DoesNotContain("hx-trigger=\"revealed\"", body, StringComparison.Ordinal); // no sentinel
    }

    // S11 — a single page of results renders NO load-more sentinel (no premature/infinite load-more).
    [Fact]
    public async Task Single_page_of_results_renders_no_load_more_sentinel()
    {
        var fake = new FakeTmdbClient { SearchTotalPages = 1, SearchTotalResults = 2 };
        fake.SearchPages[1] =
        [
            Summary(801, "Only Page A", "/a.jpg", vote: 6.0, year: 2020),
            Summary(802, "Only Page B", "/b.jpg", vote: 6.5, year: 2021),
        ];
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=dune&media=Movie&page=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Only Page A", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-trigger=\"revealed\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("page=2", body, StringComparison.Ordinal);
    }

    // S12 — a load-more (page > 1) failure returns the 200 error partial as an <li> that re-attempts JUST that
    // page in place (closest [data-search-error], outerHTML), not the whole region.
    [Fact]
    public async Task Failing_load_more_returns_200_error_li_variant()
    {
        var fake = new FakeTmdbClient { ThrowOnSearch = true };
        using var factory = CreateFactoryWith(fake);
        using var viewer = await AuthedAsync(factory);
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover/search/results?q=dune&media=Movie&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-search-error", body, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", body, StringComparison.Ordinal);
        Assert.Contains("aria-busy=\"false\"", body, StringComparison.Ordinal);
        Assert.Contains("closest [data-search-error]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-on", body, StringComparison.Ordinal);
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
        TestAuthentication.RegisterAndSignInAsync(factory, "Search Viewer");
}
