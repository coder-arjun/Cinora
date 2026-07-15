using System.Net;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Tmdb;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// Proves the standard resilience handler is actually wired onto the typed TMDB client: a transient failure
/// (HTTP 503) is retried, and the subsequent success is mapped normally. Uses the same DI registration shape
/// as the composition root — <c>AddHttpClient&lt;TmdbClient&gt;().AddStandardResilienceHandler()</c> — over a
/// fake primary handler, so no live network is involved. The same pipeline honors <c>Retry-After</c> on 429.
/// </summary>
public sealed class TmdbResilienceTests
{
    [Fact]
    public async Task StandardResilienceHandler_retries_a_transient_failure_then_maps_the_success()
    {
        using var handler = new StubHttpMessageHandler(index =>
            index == 0
                ? StubHttpMessageHandler.JsonResponse(HttpStatusCode.ServiceUnavailable, "{}")
                : StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, TmdbFixtures.Load("movie_list.json")));

        var services = new ServiceCollection();
        services
            .AddHttpClient<TmdbClient>(http => http.BaseAddress = new Uri("https://api.themoviedb.org/3/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(options =>
            {
                // Keep the retry fast and deterministic for the test while still exercising a real retry.
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
            });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<TmdbClient>();

        var summaries = await client.GetTrendingAsync(MediaType.Movie, CancellationToken.None);

        Assert.True(
            handler.CallCount >= 2,
            $"Expected at least one retry after the transient 503; handler was called {handler.CallCount} time(s).");
        Assert.NotEmpty(summaries);
    }
}
