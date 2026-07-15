using System.ComponentModel.DataAnnotations;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>Ai</c> configuration section describing the free, local Large Language Model used for
/// movie recommendations. The default provider is Ollama (native Windows, no API key, no rate limits),
/// exposed through its OpenAI-compatible <c>/v1</c> endpoint — this is deliberately NOT the paid OpenAI
/// API (CLAUDE.md free-only constraint). The <c>IRecommendationEngine</c> port keeps the provider
/// swappable. Bound with <c>ValidateDataAnnotations</c> (lazy) but NOT <c>ValidateOnStart</c> in Phase 1;
/// the consuming client (via Microsoft.Extensions.AI) arrives in Phase 5 (solution-structure.md §6).
/// </summary>
public sealed class AiOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// The LLM provider name. Defaults to <c>"Ollama"</c> (free, local). Selects which adapter the
    /// <c>IRecommendationEngine</c> implementation resolves in the phase that consumes it.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = "Ollama";

    /// <summary>
    /// The OpenAI-compatible chat-completions endpoint. Defaults to the local Ollama <c>/v1</c> API.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Endpoint { get; set; } = "http://localhost:11434/v1";

    /// <summary>The model identifier to request from the provider (e.g. a locally pulled Ollama model).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Model { get; set; } = "llama3.2";

    /// <summary>
    /// The credential sent to the OpenAI-compatible provider. Local Ollama needs no key and ignores this, so it
    /// defaults to the harmless placeholder <c>"ollama"</c>; a free-tier cloud provider (Groq, or Google Gemini's
    /// OpenAI-compatible endpoint) is enabled by setting this to a real free key alongside <see cref="Endpoint"/>
    /// and <see cref="Model"/> — with no code change (ADR 0016). Kept out of <c>appsettings.json</c>; supply it via
    /// user-secrets (dev) or an environment variable (deploy). Required non-empty so a blank key fails fast at boot.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "ollama";

    /// <summary>The per-request timeout, in seconds, for LLM calls.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>The maximum number of tokens the model may generate in a single completion.</summary>
    [Range(1, 32_000)]
    public int MaxOutputTokens { get; set; } = 1024;
}
