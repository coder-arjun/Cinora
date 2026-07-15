namespace Cinora.Application.Common.Ai;

/// <summary>
/// The input to <see cref="Interfaces.IRecommendationEngine.RankAsync"/>: the user's <see cref="Profile"/>
/// plus the <see cref="Candidates"/> — the real TMDB titles the engine may rank and explain. The engine
/// returns a ranked, explained subset drawn only from <see cref="Candidates"/>; it does not read the database,
/// call TMDB, or invent titles (ADR 0016/0017). Application-owned.
/// </summary>
public sealed record RecommendationRequest
{
    /// <summary>The user's taste profile that grounds the ranking.</summary>
    public required TasteProfile Profile { get; init; }

    /// <summary>The candidate titles the engine ranks and explains — its entire allowed output vocabulary.</summary>
    public IReadOnlyList<CandidateTitle> Candidates { get; init; } = [];
}
