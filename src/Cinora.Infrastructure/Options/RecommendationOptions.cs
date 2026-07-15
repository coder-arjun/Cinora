using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>Recommendations</c> configuration section describing the caps that bound the AI recommendation
/// pipeline — profile sizes, the candidate-pool size, the served-result count, and the reason length (Phase 5
/// design §8, ADR 0017). Infrastructure-owned (like all Options); the caps reach the Application seams through
/// the <c>IRecommendationPolicy</c> port (implemented by <c>RecommendationPolicy</c>) so the Application never
/// references this type — the same dependency-rule pattern as <c>FileStorageOptions</c>/<c>IUploadPolicy</c>
/// (ADR 0001/0013). Bound with <c>ValidateUsingDataAnnotations().ValidateOnStart()</c> in Phase 5 (first
/// consumer), so an out-of-range cap fails fast at boot.
/// </summary>
public sealed class RecommendationOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Recommendations";

    /// <summary>The maximum number of the user's highest-rated titles carried into the taste profile.</summary>
    [Range(1, 50)]
    public int MaxTopRated { get; set; } = 10;

    /// <summary>The maximum number of watchlist titles carried into the taste profile.</summary>
    [Range(1, 50)]
    public int MaxWatchlistTitles { get; set; } = 10;

    /// <summary>The maximum number of favorite genres carried into the taste profile and the discover fan-out.</summary>
    [Range(1, 25)]
    public int MaxFavoriteGenres { get; set; } = 5;

    /// <summary>The maximum number of top-rated titles used to seed the "more like this" recommendation fan-out.</summary>
    [Range(1, 25)]
    public int MaxRecommendationSeeds { get; set; } = 5;

    /// <summary>The maximum size of the deduplicated, seen-excluded candidate pool offered to the engine.</summary>
    [Range(1, 200)]
    public int MaxCandidates { get; set; } = 40;

    /// <summary>The maximum number of grounded picks kept in a served recommendation set.</summary>
    [Range(1, 100)]
    public int MaxResults { get; set; } = 12;

    /// <summary>The maximum length (in characters) of a pick's "why this" reason before it is truncated.</summary>
    [Range(1, 1000)]
    public int MaxReasonLength { get; set; } = 140;

    /// <summary>
    /// The time-to-live, in hours, of a served recommendation set in the distributed cache (Milestone 5.2, ADR
    /// 0018 §2). Defaults to 24 h — roughly one nightly-precompute refresh cycle — so a warmed set lives about one
    /// cycle before it either expires to the durable history-row fallback or is overwritten by the next run.
    /// </summary>
    [Range(1, 168)]
    public int ServedCacheTtlHours { get; set; } = 24;

    /// <summary>
    /// The lookback window, in days, that bounds the nightly precompute's active-user scan (Milestone 5.3, Phase 5
    /// design §10). Only users with review or watchlist activity within the last <c>PrecomputeLookbackDays</c> are
    /// considered for regeneration, so the set-based selection reads — and the in-memory selection dictionaries —
    /// stay bounded by recent activity rather than growing with all-time history. Defaults to 45 days.
    /// </summary>
    [Range(1, 3650)]
    public int PrecomputeLookbackDays { get; set; } = 45;
}
