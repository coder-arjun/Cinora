namespace Cinora.Application.Common.Ai;

/// <summary>
/// The token accounting the engine reports for one ranking call — the model that produced it plus the prompt
/// and completion token counts. Persisted with each <c>AIRecommendationHistory</c> row (Milestone 5.2) as the
/// cost/latency ledger and the audit of which model produced a recommendation. The counts come from the
/// model's own reported usage, not an estimate; they are 0 when the provider reports none. Application-owned.
/// </summary>
public sealed record AiUsage
{
    /// <summary>The model identifier that produced the ranking (for example a locally pulled Ollama model).</summary>
    public required string Model { get; init; }

    /// <summary>The number of prompt (input) tokens the model reported, or 0 when unreported.</summary>
    public required int PromptTokens { get; init; }

    /// <summary>The number of completion (output) tokens the model reported, or 0 when unreported.</summary>
    public required int CompletionTokens { get; init; }
}
