namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// Surfaces the recommendation pipeline's caps (profile sizes, candidate-pool size, result count, reason
/// length) to the Application seams that build and rank recommendations <b>without</b> the Application
/// referencing Infrastructure's <c>RecommendationOptions</c> — preserving the dependency rule (ADR 0001),
/// exactly mirroring the Phase-4 <see cref="IUploadPolicy"/> precedent. The taste-profile builder, the
/// candidate source, and the <c>GenerateRecommendationsCommand</c> handler read these caps here; the values
/// live in configuration (Options) and are validated at boot in Infrastructure.
/// </summary>
public interface IRecommendationPolicy
{
    /// <summary>The maximum number of the user's highest-rated titles carried into the taste profile.</summary>
    int MaxTopRated { get; }

    /// <summary>The maximum number of watchlist titles carried into the taste profile.</summary>
    int MaxWatchlistTitles { get; }

    /// <summary>The maximum number of favorite genres carried into the taste profile and the discover fan-out.</summary>
    int MaxFavoriteGenres { get; }

    /// <summary>The maximum number of top-rated titles used to seed the "more like this" recommendation fan-out.</summary>
    int MaxRecommendationSeeds { get; }

    /// <summary>The maximum size of the deduplicated, seen-excluded candidate pool offered to the engine.</summary>
    int MaxCandidates { get; }

    /// <summary>The maximum number of grounded picks kept in a served recommendation set.</summary>
    int MaxResults { get; }

    /// <summary>The maximum length (in characters) of a pick's "why this" reason before it is truncated.</summary>
    int MaxReasonLength { get; }
}
