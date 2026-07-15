using System.Net;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Ui;

/// <summary>
/// Milestone 6.5 (accessibility + animation/design polish) integration tests for the automatable UI-polish
/// slice, driven end-to-end through the real MVC + Identity pipeline against the migrated <c>CinoraTest</c>
/// LocalDB. They prove: the shared styled delete-confirm dialog (_ConfirmDialog) ships in the layout and is
/// CSP-safe; the destructive delete still requires anti-forgery (the styled confirm is a client-only gate — the
/// server token flow is unchanged); the one-way comments-collapse markup renders for a long body but not a short
/// one; and a comment's own delete affordance now uses the structured <c>data-confirm-*</c> attributes rather
/// than the native <c>hx-confirm</c>. The keyboard walkthrough, focus-trap behaviour, reduced-motion collapse,
/// View Transitions and the offline <c>htmx:sendError</c> toast are browser-behaviour — carried as manual /
/// Playwright a11y smokes in the milestone report, not asserted here.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class UiPolishTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UiPolishTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Confirm_dialog_partial_ships_in_the_layout_and_is_csp_safe()
    {
        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Confirm Dialog Viewer");
        var client = viewer.Client;

        using var response = await client.GetAsync("/discover");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The shared dialog is present with its accessible-dialog semantics and the bare-name component hook.
        Assert.Contains("id=\"confirm-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("x-data=\"confirmDialog\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-modal=\"true\"", html, StringComparison.Ordinal);

        // CSP-safe: no inline event handlers / no inline <script> introduced by the polish pass.
        Assert.DoesNotContain("hx-on", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase); // only external type="module"
    }

    [Fact]
    public async Task Deleting_a_review_still_requires_the_antiforgery_token()
    {
        using var author = await TestAuthentication.RegisterAndSignInAsync(_factory, "Confirm Author");

        // The styled confirm is a CLIENT-only gate (htmx:confirm → issueRequest); the server anti-forgery gate is
        // untouched. A hx-delete with NO RequestVerificationToken header must still be rejected (400), never run.
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/reviews/{Guid.NewGuid()}");
        request.Headers.Add("HX-Request", "true");
        using var response = await author.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Long_comment_body_renders_the_one_way_collapse_affordance()
    {
        const int tmdbId = 965_001;
        var longBody = "This is a deliberately long comment. " + new string('x', 400);
        var reviewId = await SeedReviewWithCommentAsync(tmdbId, longBody);

        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Comment Reader");
        var client = viewer.Client;
        using var response = await client.GetAsync($"/reviews/{reviewId}/comments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("x-data=\"commentCollapse\"", html, StringComparison.Ordinal); // the collapse component
        Assert.Contains("is-clamped", html, StringComparison.Ordinal);                 // the JS-gated clamp
        Assert.Contains("Show more", html, StringComparison.Ordinal);                  // the one-way toggle
        Assert.Contains("This is a deliberately long comment.", html, StringComparison.Ordinal); // full body shipped
    }

    [Fact]
    public async Task Short_comment_body_renders_no_collapse_affordance()
    {
        const int tmdbId = 965_002;
        // ASCII only: the default HtmlEncoder renders non-Basic-Latin chars (an em dash) as numeric entities,
        // which would break a naive literal-substring assertion. The collapse behaviour is what's under test.
        const string shortBody = "Loved it - a taut, gorgeous film.";
        var reviewId = await SeedReviewWithCommentAsync(tmdbId, shortBody);

        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Comment Reader");
        var client = viewer.Client;
        using var response = await client.GetAsync($"/reviews/{reviewId}/comments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("commentCollapse", html, StringComparison.Ordinal); // no component for a short body
        Assert.DoesNotContain("Show more", html, StringComparison.Ordinal);
        Assert.Contains(shortBody, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comment_delete_uses_the_styled_confirm_not_native_hx_confirm()
    {
        const int tmdbId = 965_003;
        using var author = await TestAuthentication.RegisterAndSignInAsync(_factory, "Comment Owner");
        var reviewId = await SeedReviewAsync(tmdbId, author.UserId);
        await SeedCommentAsync(reviewId, author.UserId, "A comment I can delete.");

        // GET the thread AS the author so IsMine → true and the delete affordance renders.
        using var response = await author.Client.GetAsync($"/reviews/{reviewId}/comments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The destructive control now carries the structured data-confirm-* attributes (the styled dialog) and
        // NO native hx-confirm (backlog 3.1 — window.confirm replaced app-wide).
        Assert.Contains("data-confirm-title=\"Delete comment?\"", html, StringComparison.Ordinal);
        Assert.Contains("data-confirm-action=\"Delete\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hx-confirm", html, StringComparison.Ordinal);
    }

    // ---- seeding helpers ----------------------------------------------------------------------------------

    private async Task<Guid> SeedReviewWithCommentAsync(int tmdbId, string commentBody)
    {
        var authorId = await RegisterUserAsync($"Seed Commenter {tmdbId}");
        var reviewId = await SeedReviewAsync(tmdbId, authorId);
        await SeedCommentAsync(reviewId, authorId, commentBody);
        return reviewId;
    }

    private async Task<Guid> SeedReviewAsync(int tmdbId, Guid authorId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var movie = await db.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TmdbId == tmdbId && m.MediaType == MediaType.Movie);
        Guid movieId;
        if (movie is null)
        {
            var created = Movie.FromTmdb(tmdbId, MediaType.Movie, $"Seeded Title {tmdbId}", null, null, null, null);
            db.Movies.Add(created);
            movieId = created.Id;
        }
        else
        {
            movieId = movie.Id;
        }

        var review = Review.Create(authorId, movieId, Rating.From(8), "A seeded review.");
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private async Task SeedCommentAsync(Guid reviewId, Guid authorId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Comments.Add(Comment.Create(reviewId, authorId, body));
        await db.SaveChangesAsync();
    }

    // Creates a user through the atomic registration seam (identity + domain rows share one PK — a Review/Comment
    // UserId FK chains through Users → AspNetUsers).
    private async Task<Guid> RegisterUserAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var registration = scope.ServiceProvider.GetRequiredService<IUserRegistrationService>();
        var result = await registration.RegisterAsync(
            $"seed-{Guid.NewGuid():N}@cinora.test", "Test1234!", $"{displayName} {Guid.NewGuid():N}", CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seed registration for '{displayName}' failed.");
        }

        return result.UserId;
    }
}
