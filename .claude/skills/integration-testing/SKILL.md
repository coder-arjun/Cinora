---
name: integration-testing
description: Use when writing or fixing Cinora integration tests — WebApplicationFactory setup, fake authentication, real SQL Server via Testcontainers or LocalDB, database resets between tests, anti-forgery token failures, or verifying authorization policies through the MVC pipeline.
---

# Integration Testing

## Overview
Integration tests exercise the real MVC pipeline against real SQL Server; only cross-cutting externals (authentication, TMDB, blob storage) are replaced with fakes — never the database provider.

## Quick Reference
| Task | Approach |
|---|---|
| Host | `WebApplicationFactory<Program>` + `WithWebHostBuilder` |
| Auth | Test `AuthenticationHandler<AuthenticationSchemeOptions>` issuing a `ClaimsPrincipal`; never remove `[Authorize]` |
| Database | Testcontainers `MsSqlContainer` in CI, LocalDB locally — never EF InMemory |
| Reset between tests | Respawn checkpoint (fast delete) — not drop/recreate per test |
| Anti-forgery | GET the form, extract `__RequestVerificationToken` + cookie, include both in the POST |
| Redirects | `AllowAutoRedirect = false`; assert the 302, then follow manually |
| Policies | Same request with and without the claim → expect 200 vs 403 |

## Pattern
```csharp
public class ReviewFlowTests : IClassFixture<CinoraFactory>
{
    private readonly CinoraFactory _factory;
    public ReviewFlowTests(CinoraFactory factory) => _factory = factory;

    [Fact]
    public async Task PostReview_AuthenticatedUser_RedirectsThenRendersReview()
    {
        // WHY: auto-redirect would hide the 302 we need to assert (PRG pattern)
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Arrange — anti-forgery token must come from a real GET of the form;
        // fabricated tokens fail validation with an opaque 400
        var formPage = await client.GetAsync("/movies/42/review");
        var (token, cookie) = AntiForgeryHelper.Extract(
            await formPage.Content.ReadAsStringAsync(), formPage);

        var post = new HttpRequestMessage(HttpMethod.Post, "/movies/42/review")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Rating"] = "9",
                ["Body"] = "A masterpiece.",
            }),
        };
        post.Headers.Add("Cookie", cookie);

        // Act
        var response = await client.SendAsync(post);

        // Assert — full pipeline: POST -> 302 -> the view actually renders the data
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var page = await client.GetAsync(response.Headers.Location);
        (await page.Content.ReadAsStringAsync()).Should().Contain("A masterpiece.");
    }
}
```

Register fakes in `ConfigureTestServices` inside the shared `CinoraFactory` (test auth scheme, fake TMDB client) and run migrations once per test run — script generation details in `.claude/skills/ef-core-migrations/SKILL.md`. Pure logic belongs in `.claude/skills/xunit-testing/SKILL.md`; full-browser journeys in `.claude/skills/playwright-e2e/SKILL.md`. The real auth flow being faked is described in `.claude/skills/aspnet-identity-google-oauth/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| EF InMemory "integration" tests | Real SQL Server via Testcontainers or LocalDB — the point is real behavior |
| Drop/recreate database per test | Respawn checkpoint between tests; migrate once per run |
| Default auto-redirect hiding the 302 | `AllowAutoRedirect = false`, assert, follow manually |
| POST without anti-forgery token/cookie | Extract both from a prior GET; a 400 here is not an app bug |
| Fakes in `ConfigureServices` | Use `ConfigureTestServices` — it runs after app registrations and wins |
| Removing `[Authorize]` to make tests pass | Fake the authentication handler; test policies both ways |
