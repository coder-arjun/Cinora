using Cinora.Application.Common.Ai;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The composite output of <see cref="IUserTasteProfileBuilder"/>: everything the downstream candidate source
/// and generation handler need about one user's taste, in one shot. It carries the prompt-shaped
/// <see cref="Profile"/> (which intentionally holds no TMDB ids — it is titles/genres/ratings only, §12) plus
/// the TMDB keys the grounded fan-out and the hallucination guard require:
/// <list type="bullet">
///   <item><see cref="SeedKeys"/> — the highest-rated titles' <c>(TmdbId, Media)</c>, seeds for the
///   "more like this" recommendation fan-out (ADR 0017 §1);</item>
///   <item><see cref="FavoriteGenreTmdbIds"/> — the same favorite genres' TMDB ids, for the discover fan-out;</item>
///   <item><see cref="SeenExclusions"/> — every <c>(TmdbId, Media)</c> the user reviewed or watchlisted, removed
///   from candidates so a recommendation is always a NEW title;</item>
///   <item><see cref="HasSignal"/> — false for a cold-start user (no reviews and no watchlist), letting the
///   handler skip the model entirely (§5.1).</item>
/// </list>
/// Application-owned; never a vendor or TMDB DTO.
/// </summary>
public sealed record UserTasteResult
{
    /// <summary>The compact, PII-free taste profile passed to the engine to ground ranking.</summary>
    public required TasteProfile Profile { get; init; }

    /// <summary>The highest-rated titles' TMDB keys that seed the "more like this" recommendation fan-out.</summary>
    public IReadOnlyList<(int TmdbId, MediaType Media)> SeedKeys { get; init; } = [];

    /// <summary>The favorite genres' TMDB ids used by the "in your favorite genres" discover fan-out.</summary>
    public IReadOnlyList<int> FavoriteGenreTmdbIds { get; init; } = [];

    /// <summary>Every title the user has already reviewed or watchlisted, excluded from candidate generation.</summary>
    public IReadOnlySet<(int TmdbId, MediaType Media)> SeenExclusions { get; init; } =
        new HashSet<(int TmdbId, MediaType Media)>();

    /// <summary>
    /// Whether the user has any taste signal at all (at least one review or one watchlist entry). A thin
    /// profile short-circuits generation (the engine is not called; the serve path degrades to a heuristic in
    /// Milestone 5.2).
    /// </summary>
    public bool HasSignal { get; init; }
}
