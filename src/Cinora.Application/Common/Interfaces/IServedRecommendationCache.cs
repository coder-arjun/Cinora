using Cinora.Application.Features.Recommendations;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The per-user cache of a served <see cref="RecommendationSet"/> — the low-latency serve store the For-You
/// surfaces read on every render (ADR 0018 §2, Phase 5 design §7.1). The port speaks only the Application
/// <see cref="RecommendationSet"/>; the Infrastructure adapter owns every mechanic (the per-user key, the TTL,
/// System.Text.Json serialization, and — critically — <b>graceful degradation</b>: every read/write is wrapped
/// so a cache outage returns a miss / no-ops and NEVER throws), exactly the SRP + "degrade, never crash" split
/// as <c>CachedTmdbClient</c>. The free in-memory <c>IDistributedCache</c> backs it (ADR 0004); the seam keeps a
/// Redis-compatible swap a registration-only change. The <c>GenerateRecommendationsCommand</c> writer warms it;
/// the <c>GetMyRecommendationsQuery</c> reader consumes it (and a query never writes it — CQRS purity).
/// </summary>
public interface IServedRecommendationCache
{
    /// <summary>
    /// Returns the user's cached recommendation set, or <c>null</c> on a miss (or on any cache fault, which the
    /// adapter degrades to a miss rather than surfacing as an error).
    /// </summary>
    /// <param name="userId">The user whose served set to read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The cached set, or <c>null</c> when none is cached (or the cache is unavailable).</returns>
    Task<RecommendationSet?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Warms the user's served set under the per-user key with the configured TTL. Best-effort: a cache write
    /// failure is swallowed by the adapter (the caller's durable history row already persisted the set), so this
    /// never throws.
    /// </summary>
    /// <param name="userId">The user whose served set to warm.</param>
    /// <param name="recommendationSet">The recommendation set to cache.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the write has been attempted.</returns>
    Task SetAsync(Guid userId, RecommendationSet recommendationSet, CancellationToken cancellationToken);
}
