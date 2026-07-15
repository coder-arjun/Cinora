using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Ai;

/// <summary>
/// A single real, resolvable TMDB title offered to the recommendation engine as a candidate to rank. The
/// candidate set is the model's entire allowed output vocabulary (ADR 0017 grounding): the engine may only
/// rank and explain these — it never invents a title. Because every candidate carries a real
/// <see cref="TmdbId"/> and <see cref="Media"/>, every legitimate pick resolves by construction, and the
/// caller's hallucination guard drops anything the model returns that is not in this set. Application-owned;
/// candidate generation (from the user's own taste, via TMDB) arrives in Milestone 5.1 — the engine simply
/// ranks whatever set it is handed.
/// </summary>
public sealed record CandidateTitle
{
    /// <summary>The TMDB identifier the model echoes back to identify a pick.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the candidate is a movie or a series.</summary>
    public required MediaType Media { get; init; }

    /// <summary>The display title.</summary>
    public required string Title { get; init; }

    /// <summary>The release year, or <c>null</c> when unknown.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// The raw TMDB poster image path (for example <c>/abc.jpg</c>), or <c>null</c> when none exists. Carried
    /// so the served recommendation card renders a poster straight from the grounded candidate — the picked
    /// title's metadata always comes from the candidate, never from the model's free text (ADR 0017 §3). Not
    /// sent to the model (it plays no part in ranking).
    /// </summary>
    public string? PosterPath { get; init; }

    /// <summary>The genre names tagged on the title.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];
}
