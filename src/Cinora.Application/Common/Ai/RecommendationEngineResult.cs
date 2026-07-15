namespace Cinora.Application.Common.Ai;

/// <summary>
/// The output of <see cref="Interfaces.IRecommendationEngine.RankAsync"/>: the ranked, explained
/// <see cref="Picks"/> (a subset of the request's candidates, best match first) and the <see cref="Usage"/>
/// the model reported. An empty <see cref="Picks"/> list is a valid, non-exceptional result — it means the
/// model returned nothing usable (an empty or malformed body), and the caller degrades to its fallback
/// (Milestone 5.2) rather than treating it as an error. Application-owned.
/// </summary>
public sealed record RecommendationEngineResult
{
    /// <summary>The ranked picks, best match first; empty when the model returned nothing usable.</summary>
    public IReadOnlyList<RankedPick> Picks { get; init; } = [];

    /// <summary>The token usage the model reported for this ranking call.</summary>
    public required AiUsage Usage { get; init; }
}
