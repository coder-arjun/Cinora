using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Recommendations;

/// <summary>Whether a served recommendation set came from the AI engine or the deterministic fallback heuristic.</summary>
public enum RecommendationSource
{
    /// <summary>Produced by the grounded LLM ranking (the primary path).</summary>
    Ai = 0,

    /// <summary>Produced by the deterministic non-AI heuristic (cold-start / model-outage fallback; Milestone 5.2).</summary>
    Heuristic = 1,
}

/// <summary>
/// A served recommendation set: the ordered <see cref="Picks"/> (best match first), the <see cref="Source"/>
/// that produced them, and when they were generated. Application-owned; carries no Domain entity and no vendor
/// type. In Milestone 5.1 a successful AI generation yields <see cref="RecommendationSource.Ai"/>; an empty set
/// is likewise <see cref="RecommendationSource.Ai"/> with no picks (the <see cref="RecommendationSource.Heuristic"/>
/// fallback is introduced in Milestone 5.2).
/// </summary>
public sealed record RecommendationSet
{
    /// <summary>The recommended titles, best match first; empty when generation produced nothing usable.</summary>
    public IReadOnlyList<RecommendationPick> Picks { get; init; } = [];

    /// <summary>Which pipeline produced the set (AI or heuristic).</summary>
    public required RecommendationSource Source { get; init; }

    /// <summary>The UTC instant the set was generated.</summary>
    public required DateTime GeneratedAtUtc { get; init; }
}

/// <summary>
/// One recommended title in a served set. Its card metadata (<see cref="Title"/>, <see cref="PosterPath"/>,
/// <see cref="ReleaseYear"/>, <see cref="Media"/>) always comes from the grounded candidate — never from the
/// model's free text; only <see cref="Reason"/> (the "why this" caption) is the model's, bounded to the policy's
/// reason length (ADR 0017 §3).
/// </summary>
public sealed record RecommendationPick
{
    /// <summary>The TMDB identifier of the recommended title (resolves on every title surface).</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the title is a movie or a series.</summary>
    public required MediaType Media { get; init; }

    /// <summary>The display title (from the candidate, not the model).</summary>
    public required string Title { get; init; }

    /// <summary>The raw TMDB poster path, or <c>null</c> when none exists.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The release year, or <c>null</c> when unknown.</summary>
    public int? ReleaseYear { get; init; }

    /// <summary>The short "why this" explanation the model wrote for the pick, truncated to the policy cap.</summary>
    public required string Reason { get; init; }
}
