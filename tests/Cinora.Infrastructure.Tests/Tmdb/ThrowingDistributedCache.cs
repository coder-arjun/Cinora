using Microsoft.Extensions.Caching.Distributed;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// A fake <see cref="IDistributedCache"/> whose every operation throws — modelling a cache outage. Used to
/// prove the <see cref="Cinora.Infrastructure.Tmdb.CachedTmdbClient"/> degrades gracefully: a get/set that
/// throws is logged and the request still resolves from the inner client (ADR 0007 "degrade, never error").
/// </summary>
internal sealed class ThrowingDistributedCache : IDistributedCache
{
    private static InvalidOperationException Down() => new("The cache is unavailable.");

    /// <inheritdoc />
    public byte[]? Get(string key) => throw Down();

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down();

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down();

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Down();

    /// <inheritdoc />
    public void Refresh(string key) => throw Down();

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => throw Down();

    /// <inheritdoc />
    public void Remove(string key) => throw Down();

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default) => throw Down();
}
