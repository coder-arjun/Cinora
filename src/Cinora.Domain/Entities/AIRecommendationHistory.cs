using Cinora.Domain.Common;

namespace Cinora.Domain.Entities;

/// <summary>
/// An audit record of one AI recommendation generation (Phase 5): which model ran, a summary of the
/// grounding input and generated output, and the token usage. Enables cost/latency tracking and
/// lets the app avoid regenerating identical recommendations.
/// </summary>
public sealed class AIRecommendationHistory
{
    /// <summary>The maximum allowed length of the <see cref="Model"/> name.</summary>
    public const int ModelMaxLength = 200;

    /// <summary>The maximum allowed length of an input or output summary.</summary>
    public const int SummaryMaxLength = 4000;

    private AIRecommendationHistory()
    {
    }

    /// <summary>The unique identifier of the history record.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the user the recommendation was generated for.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The name/identifier of the model that produced the recommendation.</summary>
    public string Model { get; private set; } = null!;

    /// <summary>A summary of the grounding input sent to the model.</summary>
    public string InputSummary { get; private set; } = null!;

    /// <summary>A summary of the recommendation the model produced.</summary>
    public string OutputSummary { get; private set; } = null!;

    /// <summary>The number of prompt (input) tokens consumed.</summary>
    public int PromptTokens { get; private set; }

    /// <summary>The number of completion (output) tokens produced.</summary>
    public int CompletionTokens { get; private set; }

    /// <summary>The UTC instant the recommendation was generated.</summary>
    public DateTime GeneratedAtUtc { get; private set; }

    /// <summary>Creates a new AI recommendation history record.</summary>
    /// <param name="userId">The target user's identifier.</param>
    /// <param name="model">The model name; required, max <see cref="ModelMaxLength"/> characters.</param>
    /// <param name="inputSummary">A summary of the grounding input; required, max <see cref="SummaryMaxLength"/> characters.</param>
    /// <param name="outputSummary">A summary of the produced recommendation; required, max <see cref="SummaryMaxLength"/> characters.</param>
    /// <param name="promptTokens">The prompt token count; must be zero or greater.</param>
    /// <param name="completionTokens">The completion token count; must be zero or greater.</param>
    /// <returns>A new <see cref="AIRecommendationHistory"/> record.</returns>
    public static AIRecommendationHistory Create(
        Guid userId,
        string model,
        string inputSummary,
        string outputSummary,
        int promptTokens,
        int completionTokens) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Model = Guard.Required(model, ModelMaxLength, nameof(model)),
            InputSummary = Guard.Required(inputSummary, SummaryMaxLength, nameof(inputSummary)),
            OutputSummary = Guard.Required(outputSummary, SummaryMaxLength, nameof(outputSummary)),
            PromptTokens = Guard.NonNegative(promptTokens, nameof(promptTokens)),
            CompletionTokens = Guard.NonNegative(completionTokens, nameof(completionTokens)),
            GeneratedAtUtc = DateTime.UtcNow,
        };
}
