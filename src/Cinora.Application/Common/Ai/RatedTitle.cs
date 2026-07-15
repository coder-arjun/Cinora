namespace Cinora.Application.Common.Ai;

/// <summary>
/// One title the user has rated, projected into the taste profile that grounds a recommendation request.
/// Carries only what the model needs to infer taste — the display title, release year, the user's rating, and
/// genre names — and deliberately no identifiers or personal data (the Phase 5 §12 prompt-hygiene rule). Part
/// of the <see cref="Interfaces.IRecommendationEngine"/> contract; Application-owned, never a vendor or TMDB
/// DTO. Reusing genre <em>names</em> (not ids) keeps the profile human-readable for the model and the audit log.
/// </summary>
public sealed record RatedTitle
{
    /// <summary>The display title of the rated movie or series.</summary>
    public required string Title { get; init; }

    /// <summary>The release year, or <c>null</c> when unknown.</summary>
    public int? Year { get; init; }

    /// <summary>The user's rating on Cinora's 1–10 scale.</summary>
    public required int Rating { get; init; }

    /// <summary>The genre names tagged on the title (for example "Drama", "Sci-Fi").</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];
}
