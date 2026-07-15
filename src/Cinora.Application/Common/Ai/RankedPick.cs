using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Ai;

/// <summary>
/// One title the engine chose from the candidate set, identified by the exact <see cref="TmdbId"/> and
/// <see cref="Media"/> the caller offered, with a short natural-language <see cref="Reason"/> explaining the
/// match (the "why this" caption the UI shows). The identity is the candidate's, so the caller's hallucination
/// guard is a simple set-membership check and the served card metadata (title/poster/year) comes from the
/// candidate — never from the model's free text. Application-owned.
/// </summary>
public sealed record RankedPick
{
    /// <summary>The TMDB identifier of the picked candidate.</summary>
    public required int TmdbId { get; init; }

    /// <summary>Whether the pick is a movie or a series.</summary>
    public required MediaType Media { get; init; }

    /// <summary>A short, one-sentence explanation of why the title matches the user's taste.</summary>
    public required string Reason { get; init; }
}
