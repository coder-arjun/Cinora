using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>Cache</c> configuration section describing the caching backend behind the
/// <c>IDistributedCache</c> abstraction. The free default provider is <c>"Memory"</c>
/// (<c>AddDistributedMemoryCache</c>, in-process, no dependencies); a Redis-compatible server such as
/// Memurai Developer is optional and only engaged when <see cref="Provider"/> is set to Redis, in which
/// case <see cref="RedisConnectionString"/> supplies the connection (CLAUDE.md free-only constraint).
/// Bound with <c>ValidateDataAnnotations</c> (lazy) but NOT <c>ValidateOnStart</c> in Phase 1; caching
/// wiring that consumes these values arrives in a later phase (solution-structure.md §6).
/// </summary>
public sealed class CacheOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// The cache provider name. Defaults to <c>"Memory"</c> (free, in-process). Set to a Redis-compatible
    /// provider (e.g. Memurai) to use a distributed cache; <see cref="RedisConnectionString"/> is then required.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = "Memory";

    /// <summary>
    /// The connection string for the Redis-compatible server. Optional — only read when
    /// <see cref="Provider"/> selects Redis; ignored (and typically absent) for the in-memory default.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>The default cache entry time-to-live, in seconds, applied when a caller does not specify one.</summary>
    [Range(1, 86_400)]
    public int DefaultTtlSeconds { get; set; } = 300;
}
