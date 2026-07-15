using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Tmdb;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// Verifies the cache-aside behaviour of <see cref="CachedTmdbClient"/> against a real in-memory
/// <see cref="MemoryDistributedCache"/> and a call-counting <see cref="FakeTmdbClient"/>: identical calls hit
/// the cache (the inner client is called once), distinct arguments use distinct keys, null/empty misses are
/// never cached, and a throwing cache degrades to the inner client without surfacing an error.
/// </summary>
public sealed class CachedTmdbClientTests
{
    // Microsoft.Extensions.Options.Options is fully qualified because the Cinora.Infrastructure.Options
    // namespace (source of CacheOptions) otherwise shadows the framework's Options helper type.
    // Returns the concrete type (CA1859); MemoryDistributedCache is a real IDistributedCache — no fakery.
    private static MemoryDistributedCache MemoryCache() =>
        new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static CachedTmdbClient CreateSut(ITmdbClient inner, IDistributedCache cache) =>
        new(inner, cache, Microsoft.Extensions.Options.Options.Create(new CacheOptions()), NullLogger<CachedTmdbClient>.Instance);

    // ---- Cache hit: a second identical call is served from the cache (inner called once) ----

    [Fact]
    public async Task GetTrendingAsync_second_identical_call_is_served_from_the_cache()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        var first = await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);
        var second = await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(1, inner.GetTrendingCount);
        Assert.Equal(first[0].TmdbId, second[0].TmdbId);
        Assert.Equal(first[0].Title, second[0].Title);
    }

    [Fact]
    public async Task GetDetailsAsync_second_identical_call_is_served_from_the_cache()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        var first = await sut.GetDetailsAsync(MediaType.Movie, 27205, CancellationToken.None);
        var second = await sut.GetDetailsAsync(MediaType.Movie, 27205, CancellationToken.None);

        Assert.Equal(1, inner.GetDetailsCount);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.TmdbId, second!.TmdbId);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Runtime, second.Runtime);
        Assert.Equal(first.Genres, second.Genres); // flat records round-trip through JSON
    }

    [Fact]
    public async Task SearchAsync_second_identical_call_is_served_from_the_cache()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        var first = await sut.SearchAsync(MediaType.Movie, "inception", 1, CancellationToken.None);
        var second = await sut.SearchAsync(MediaType.Movie, "inception", 1, CancellationToken.None);

        Assert.Equal(1, inner.SearchCount);
        Assert.Equal(first.TotalResults, second.TotalResults);
        Assert.Equal(first.Items[0].TmdbId, second.Items[0].TmdbId);
    }

    [Fact]
    public async Task GetGenresAsync_second_identical_call_is_served_from_the_cache()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.GetGenresAsync(MediaType.Movie, CancellationToken.None);
        await sut.GetGenresAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(1, inner.GetGenresCount);
    }

    // ---- Distinct keys: different arguments do not collide ----

    [Fact]
    public async Task GetDetailsAsync_distinct_id_and_media_use_distinct_keys()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.GetDetailsAsync(MediaType.Movie, 1, CancellationToken.None);
        await sut.GetDetailsAsync(MediaType.Movie, 2, CancellationToken.None);   // different id
        await sut.GetDetailsAsync(MediaType.Series, 1, CancellationToken.None);  // different media, same id

        Assert.Equal(3, inner.GetDetailsCount);
    }

    [Fact]
    public async Task SearchAsync_distinct_query_page_and_media_use_distinct_keys()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.SearchAsync(MediaType.Movie, "batman", 1, CancellationToken.None);
        await sut.SearchAsync(MediaType.Movie, "batman", 2, CancellationToken.None);   // different page
        await sut.SearchAsync(MediaType.Movie, "superman", 1, CancellationToken.None); // different query
        await sut.SearchAsync(MediaType.Series, "batman", 1, CancellationToken.None);  // different media

        Assert.Equal(4, inner.SearchCount);
    }

    [Fact]
    public async Task Rails_of_different_media_use_distinct_keys()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);
        await sut.GetTrendingAsync(MediaType.Series, CancellationToken.None);

        Assert.Equal(2, inner.GetTrendingCount);
    }

    [Fact]
    public async Task Different_rails_of_the_same_media_use_distinct_keys()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);
        await sut.GetPopularAsync(MediaType.Movie, CancellationToken.None);
        await sut.GetTopRatedAsync(MediaType.Movie, CancellationToken.None);

        Assert.Equal(1, inner.GetTrendingCount);
        Assert.Equal(1, inner.GetPopularCount);
        Assert.Equal(1, inner.GetTopRatedCount);
    }

    [Fact]
    public async Task SearchAsync_normalizes_case_and_whitespace_to_a_single_key()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, MemoryCache());

        await sut.SearchAsync(MediaType.Movie, "Inception", 1, CancellationToken.None);
        await sut.SearchAsync(MediaType.Movie, "  inception  ", 1, CancellationToken.None); // trims + case-folds

        Assert.Equal(1, inner.SearchCount);
    }

    // ---- Never cache a null / empty miss ----

    [Fact]
    public async Task GetDetailsAsync_null_result_is_not_cached()
    {
        var inner = new FakeTmdbClient { DetailsResult = null }; // model a 404
        var sut = CreateSut(inner, MemoryCache());

        var first = await sut.GetDetailsAsync(MediaType.Movie, 999, CancellationToken.None);
        var second = await sut.GetDetailsAsync(MediaType.Movie, 999, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, inner.GetDetailsCount); // the null was NOT cached — inner hit both times
    }

    [Fact]
    public async Task SearchAsync_empty_result_is_not_cached()
    {
        var inner = new FakeTmdbClient { SearchResult = FakeTmdbClient.EmptyPage() };
        var sut = CreateSut(inner, MemoryCache());

        await sut.SearchAsync(MediaType.Movie, "no-such-title", 1, CancellationToken.None);
        await sut.SearchAsync(MediaType.Movie, "no-such-title", 1, CancellationToken.None);

        Assert.Equal(2, inner.SearchCount); // the empty page was NOT cached — a later populate is picked up
    }

    // ---- Graceful degradation: a throwing cache never fails the request ----

    [Fact]
    public async Task GetDetailsAsync_survives_a_throwing_cache_and_returns_the_inner_result()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, new ThrowingDistributedCache());

        var details = await sut.GetDetailsAsync(MediaType.Movie, 27205, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(27205, details!.TmdbId);
        Assert.Equal(1, inner.GetDetailsCount);
    }

    [Fact]
    public async Task GetTrendingAsync_survives_a_throwing_cache_on_every_call()
    {
        var inner = new FakeTmdbClient();
        var sut = CreateSut(inner, new ThrowingDistributedCache());

        var first = await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);
        var second = await sut.GetTrendingAsync(MediaType.Movie, CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Equal(2, inner.GetTrendingCount); // nothing is cached, so both calls reach the inner client
    }
}
