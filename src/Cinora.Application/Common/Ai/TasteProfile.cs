namespace Cinora.Application.Common.Ai;

/// <summary>
/// A compact, PII-free summary of a single user's demonstrated taste, built from their own reviews, ratings,
/// and watchlist (Milestone 5.1) and passed to the <see cref="Interfaces.IRecommendationEngine"/> to ground
/// ranking. Bounded on purpose — a handful of top-rated titles, watchlist titles, and favorite genres — to
/// keep the prompt's input size small (the Phase 5 §8 token controls) and to carry no user id, display name,
/// or email (the §12 prompt-hygiene rule). Application-owned; never a vendor or TMDB DTO.
/// </summary>
public sealed record TasteProfile
{
    /// <summary>The user's highest-rated reviewed titles, with their ratings and genres.</summary>
    public IReadOnlyList<RatedTitle> TopRated { get; init; } = [];

    /// <summary>Titles the user has planned or is watching — a positive-intent signal.</summary>
    public IReadOnlyList<string> WatchlistTitles { get; init; } = [];

    /// <summary>The user's most-frequent genres across their ratings and watchlist.</summary>
    public IReadOnlyList<string> FavoriteGenres { get; init; } = [];
}
