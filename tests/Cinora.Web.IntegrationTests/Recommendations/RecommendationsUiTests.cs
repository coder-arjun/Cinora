using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace Cinora.Web.IntegrationTests.Recommendations;

/// <summary>
/// Milestone 5.4 mock-first tests for the "For You" recommendation UI (Phase 5 design §11/§13/§15), driven
/// end-to-end through the real MVC + hand-rolled <c>ISender</c> + Identity pipeline against the migrated
/// <c>CinoraTest</c> LocalDB. The serve path is exercised through the actual <see cref="RecommendationsController"/>:
/// the authed page + rail render cards from a seeded history row (tier b) or the deterministic heuristic (tier c),
/// and a <see cref="FakeRecommendationEngine"/> is injected purely to prove the headline exit criterion — <b>every
/// For-You read makes ZERO LLM calls</b> (<c>CallCount == 0</c>). The dismiss endpoint is fail-closed
/// (anonymous → 302) and anti-forgery-guarded (authed without the token → 400; with it → an empty 200 so HTMX
/// removes the card). TMDB ids sit in the 975_xxx block so the shared database stays isolated from the sibling
/// suites.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class RecommendationsUiTests : IAsyncLifetime
{
    private const int PickATmdbId = 975_010;
    private const int PickBTmdbId = 975_011;
    private const int PickCTmdbId = 975_012;
    private const int PopularTmdbId = 975_050;
    private const int DismissTmdbId = 975_070;

    // The model-authored "why this" reason the seeded AI set carries; asserted verbatim (no HTML-special chars,
    // so default Razor encoding renders it as-is) to prove the explanation reaches the card.
    private const string ReasonA = "Because you loved gritty neo-noir thrillers.";

    // An UNTRUSTED, model-authored reason full of HTML-special chars, seeded on a third pick to guard the
    // output-encoding property: @Model.Reason must emit the HTML-ENCODED form and NEVER the live markup. A
    // regressed @Html.Raw(Model.Reason) (stored XSS) would emit the raw <script> and fail the R1 assertions.
    private const string ReasonXss = "<script>alert(1)</script> & \"noir\"";
    // The HTML-encoded form Razor's default encoder produces for the <script>…</script> fragment above.
    private const string ReasonXssEncodedScript = "&lt;script&gt;alert(1)&lt;/script&gt;";
    // The raw markup that must be ABSENT — its presence would mean the reason was emitted unencoded.
    private const string ReasonXssRawScript = "<script>alert(1)</script>";

    private readonly CinoraWebApplicationFactory _factory;

    public RecommendationsUiTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // R1 — the authed page renders the FULL page (layout) with a card, the "why this" reason, a w342 TMDB poster
    // URL, the Details link, and the dismiss wiring; the serve makes NO LLM call. Also guards two properties:
    // (a) the AI-UNIQUE eyebrow ("Recommended for you") renders — not the ambient "For You" (nav link + <title>);
    // (b) an untrusted reason with HTML-special chars is HTML-ENCODED, never emitted raw (a @Html.Raw regression
    //     would fail it).
    [Fact]
    public async Task Page_authed_renders_full_page_with_card_reason_poster_and_details_link()
    {
        var fakeEngine = new FakeRecommendationEngine();
        using var factory = CreateFactoryWith(new FakeTmdbClient(), fakeEngine);
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Rita Recs 975");
        await SeedAiHistoryAsync(factory, user.UserId);

        using var response = await user.Client.GetAsync("/recommendations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);   // the FULL page (layout rendered)
        Assert.Contains("Recommended for you", body, StringComparison.Ordinal); // the AI-UNIQUE eyebrow (not ambient)
        Assert.Contains("For You Alpha 975", body, StringComparison.Ordinal); // a rendered card title
        Assert.Contains(ReasonA, body, StringComparison.Ordinal);             // the model's "why this" explanation
        Assert.Contains(
            "https://images.weserv.nl/?url=image.tmdb.org/t/p/w342/foryou-a.jpg", body, StringComparison.Ordinal); // the w342 poster, via the weserv proxy
        Assert.Contains(
            $"/discover/title/movie/{PickATmdbId}", body, StringComparison.Ordinal);          // the Details link
        Assert.Contains(
            $"/recommendations/{PickATmdbId}/dismiss", body, StringComparison.Ordinal);       // the dismiss wiring

        // XSS (M1): the untrusted reason is HTML-ENCODED, and the live <script> markup is ABSENT. A regressed
        // @Html.Raw(Model.Reason) would emit the raw payload and flip BOTH of these.
        Assert.Contains(ReasonXssEncodedScript, body, StringComparison.Ordinal);   // encoded form present
        Assert.DoesNotContain(ReasonXssRawScript, body, StringComparison.Ordinal); // raw markup absent

        // UI L4: the populated page ships the hidden live-empty stub site.ts reveals when every card is dismissed.
        Assert.Contains("data-rec-live-empty", body, StringComparison.Ordinal);

        Assert.Equal(0, fakeEngine.CallCount); // the serve path is LLM-free (the headline exit criterion)
    }

    // R2 — the rail endpoint returns a PARTIAL (no layout) with the cards and clears the shell's busy state; no LLM.
    [Fact]
    public async Task Rail_authed_returns_partial_with_cards_and_no_llm_call()
    {
        var fakeEngine = new FakeRecommendationEngine();
        using var factory = CreateFactoryWith(new FakeTmdbClient(), fakeEngine);
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Ray Rail 975");
        await SeedAiHistoryAsync(factory, user.UserId);

        using var response = await user.Client.GetAsync("/recommendations/rail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase); // a partial, not a full document
        Assert.Contains("For You Alpha 975", body, StringComparison.Ordinal);     // a rendered card
        Assert.Contains(ReasonA, body, StringComparison.Ordinal);                 // the "why this" caption
        Assert.Contains($"/discover/title/movie/{PickATmdbId}", body, StringComparison.Ordinal);
        Assert.Contains("aria-busy=\"false\"", body, StringComparison.Ordinal);   // clears the shell's loading state

        Assert.Equal(0, fakeEngine.CallCount);
    }

    // R3 — the page is fail-closed: an anonymous GET is challenged to the login page (no [AllowAnonymous]).
    [Fact]
    public async Task Anonymous_page_redirects_to_login()
    {
        using var anon = TestAuthentication.CreateClient(_factory);

        using var response = await anon.GetAsync("/recommendations");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/account/login",
            response.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // R4 — dismiss is fail-closed AND anti-forgery-guarded: anonymous → 302 login; authed but missing the token →
    // 400; authed with the token (+ HX-Request) → an empty 200 so hx-swap="outerHTML" removes the card.
    [Fact]
    public async Task Dismiss_requires_auth_and_token_then_returns_empty_200()
    {
        // Anonymous POST → 302 to /account/login (the global fallback authorization is fail-closed).
        using var anon = TestAuthentication.CreateClient(_factory);
        using var anonContent = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["media"] = nameof(MediaType.Movie) });
        using var anonPost = await anon.PostAsync($"/recommendations/{DismissTmdbId}/dismiss", anonContent);
        Assert.Equal(HttpStatusCode.Redirect, anonPost.StatusCode);
        Assert.Contains(
            "/account/login",
            anonPost.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Dana Dismiss 975");

        // Authenticated but WITHOUT the anti-forgery token → 400 (the global AutoValidateAntiforgeryToken).
        using var noTokenContent = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["media"] = nameof(MediaType.Movie) });
        using var noToken = await user.Client.PostAsync($"/recommendations/{DismissTmdbId}/dismiss", noTokenContent);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        // Authenticated WITH the token (as the RequestVerificationToken header site.ts sends) + HX-Request → 200
        // empty. The empty body is what hx-swap="outerHTML" needs to drop the card's <li>.
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/discover");
        using var okContent = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["media"] = nameof(MediaType.Movie) });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/recommendations/{DismissTmdbId}/dismiss")
        {
            Content = okContent,
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        using var ok = await user.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(string.Empty, await ok.Content.ReadAsStringAsync());
    }

    // R5 — a served HEURISTIC set (fresh user, no history/cache, faked popular titles) labels the page honestly as
    // "Popular picks" (never an error), and still makes no LLM call.
    [Fact]
    public async Task Heuristic_serve_labels_the_page_popular_picks()
    {
        var fakeEngine = new FakeRecommendationEngine();
        var fakeTmdb = new FakeTmdbClient { PopularMovies = [Summary(PopularTmdbId, "Popular Pick 975")] };
        using var factory = CreateFactoryWith(fakeTmdb, fakeEngine);
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Holly Heuristic 975");
        // No history + no taste signal → the serve falls all the way to the deterministic heuristic.

        using var response = await user.Client.GetAsync("/recommendations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Popular picks", body, StringComparison.Ordinal);    // the honest heuristic label
        Assert.Contains("Popular Pick 975", body, StringComparison.Ordinal); // a rendered heuristic card
        Assert.Equal(0, fakeEngine.CallCount);
    }

    // R6 — an EMPTY served set (fresh user, no history, no popular titles) renders the honest empty state, NOT an
    // error/500.
    [Fact]
    public async Task Empty_serve_renders_the_honest_empty_state_not_an_error()
    {
        var fakeEngine = new FakeRecommendationEngine();
        // No canned popular/top-rated lists → the heuristic yields an empty set for a fresh user.
        using var factory = CreateFactoryWith(new FakeTmdbClient(), fakeEngine);
        using var user = await TestAuthentication.RegisterAndSignInAsync(factory, "Ed Empty 975");

        using var response = await user.Client.GetAsync("/recommendations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // never a 500
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-rec-empty", body, StringComparison.Ordinal);          // the honest empty state
        Assert.DoesNotContain("For You Alpha 975", body, StringComparison.Ordinal); // no phantom cards
        Assert.Equal(0, fakeEngine.CallCount);
    }

    // Seeds one AIRecommendationHistory row with a self-contained AI envelope so the serve path's tier-b fallback
    // (cache miss → latest history row) renders cards with NO TMDB or LLM call.
    private static async Task SeedAiHistoryAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        var set = new RecommendationSet
        {
            Source = RecommendationSource.Ai,
            GeneratedAtUtc = DateTime.UtcNow,
            Picks =
            [
                new RecommendationPick
                {
                    TmdbId = PickATmdbId,
                    Media = MediaType.Movie,
                    Title = "For You Alpha 975",
                    PosterPath = "/foryou-a.jpg",
                    ReleaseYear = 2019,
                    Reason = ReasonA,
                },
                new RecommendationPick
                {
                    TmdbId = PickBTmdbId,
                    Media = MediaType.Movie,
                    Title = "For You Beta 975",
                    PosterPath = "/foryou-b.jpg",
                    ReleaseYear = 2021,
                    Reason = "A slow-burn mystery in your favourite genre.",
                },
                // A served pick whose untrusted reason carries HTML-special chars, so the encoding property is
                // actually exercised end-to-end (the reason reaches the rendered card via @Model.Reason).
                new RecommendationPick
                {
                    TmdbId = PickCTmdbId,
                    Media = MediaType.Movie,
                    Title = "For You Gamma 975",
                    PosterPath = "/foryou-c.jpg",
                    ReleaseYear = 2020,
                    Reason = ReasonXss,
                },
            ],
        };
        var envelope = RecommendationSetEnvelope.Serialize(set, AIRecommendationHistory.SummaryMaxLength);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Set<AIRecommendationHistory>().Add(
            AIRecommendationHistory.Create(userId, "fake-integration", "seed grounding summary", envelope, 10, 5));
        await db.SaveChangesAsync();
    }

    // Builds a host with the two external boundaries faked (TMDB + the engine) — leaving the shared factory
    // untouched — and restores the process-global Serilog logger the host build installs (the sibling-suite guard,
    // matching RecommendationServeTests / DiscoveryRailTests in this non-parallel collection).
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

    private static TmdbTitleSummary Summary(int tmdbId, string title) =>
        new()
        {
            TmdbId = tmdbId,
            MediaType = MediaType.Movie,
            Title = title,
            PosterPath = "/pop.jpg",
            ReleaseDate = new DateOnly(2020, 1, 1),
        };
}
