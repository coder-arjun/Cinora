namespace Cinora.Application.Common.Exceptions;

/// <summary>
/// Thrown by an <see cref="Interfaces.IRecommendationEngine"/> implementation when the recommendation model is
/// unavailable — a transport/connection failure or the per-request timeout — so the caller can degrade to its
/// fallback (the newest recommendation-history row, then a deterministic heuristic; Milestone 5.2) rather than
/// failing. A malformed or empty model response is deliberately NOT signalled with this exception: that yields
/// an empty result and the engine degrades in place. Generation runs off the request path (a background job),
/// so this never surfaces to a page render; if a future on-demand refresh endpoint dispatches generation
/// inline, the Web layer's global exception handler maps it to a friendly 503 (never a 500 stack trace).
/// </summary>
public sealed class RecommendationEngineException : Exception
{
    /// <summary>Initializes a new <see cref="RecommendationEngineException"/> with a default message.</summary>
    public RecommendationEngineException()
        : base("The recommendation model was unavailable.")
    {
    }

    /// <summary>Initializes a new <see cref="RecommendationEngineException"/> with the specified message.</summary>
    /// <param name="message">A human-readable description of the failure.</param>
    public RecommendationEngineException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="RecommendationEngineException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable description of the failure.</param>
    /// <param name="innerException">The underlying exception that caused this one.</param>
    public RecommendationEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
