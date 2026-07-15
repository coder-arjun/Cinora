using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Ai;

/// <summary>
/// The free, local <see cref="IRecommendationEngine"/> adapter (ADR 0016). It ranks and explains a supplied
/// candidate set with a Large Language Model reached through a Microsoft.Extensions.AI <see cref="IChatClient"/>
/// — the default provider is a local Ollama model over its OpenAI-compatible <c>/v1</c> endpoint (no API key,
/// no rate limits, nothing leaves the machine). It builds a compact, PII-free prompt from the Application
/// <see cref="RecommendationRequest"/>, asks for structured JSON at a low temperature under a per-request
/// timeout, then parses and validates the response into <see cref="RankedPick"/>s. The vendor types
/// (<see cref="IChatClient"/>, <see cref="ChatMessage"/>, <see cref="ChatOptions"/>) stay entirely inside this
/// class; the port speaks only Cinora types (ADR 0016). Two failure postures, deliberately different:
/// <list type="bullet">
///   <item>a transport failure or the per-request timeout throws <see cref="RecommendationEngineException"/>
///   so the caller degrades to its fallback (ADR 0018);</item>
///   <item>a malformed or empty model body is NOT an error — it degrades in place to an empty pick set (the
///   caller treats "no picks" as a generation miss and falls back), mirroring <c>CachedTmdbClient</c>'s
///   "degrade, never crash" posture.</item>
/// </list>
/// Internal: the composition root registers it behind the Application port. Candidate generation and the
/// authoritative hallucination guard belong to Milestone 5.1 — this adapter simply ranks whatever set it is
/// handed (and defensively drops any off-list pick anyway).
/// </summary>
internal sealed class OllamaRecommendationEngine : IRecommendationEngine
{
    // Low temperature → repeatable, on-taste rankings. Slightly tighter than the skill's 0.4 because grounding
    // (the fixed candidate pool) already supplies variety; the model only needs to order and explain it (§8).
    private const float DefaultTemperature = 0.3f;

    // A defensive per-reason cap so a chatty small model cannot emit an unbounded "why this" caption; the
    // authoritative ≈140-char cap is applied by the Milestone 5.1 guard when the served set is built.
    private const int MaxReasonLength = 240;

    // The model's whole job: from the numbered candidates only, order and explain the best matches as strict
    // JSON, keyed by each candidate's id. The candidate list is the model's entire allowed output vocabulary.
    private const string SystemPrompt =
        "You are Cinora's movie and series recommender. From the numbered CANDIDATES only, choose the titles "
        + "that best match the user's taste and explain each in one short sentence. Never invent a title and "
        + "never choose one that is not in the list. Identify each pick by its candidate id. Order the picks "
        + "best match first. Respond with JSON only, in exactly this shape: "
        + "{\"picks\":[{\"id\":<candidate id>,\"reason\":\"<one short sentence>\"}]}.";

    // Web defaults give case-insensitive matching (a small model may vary casing); trailing commas and comment
    // skipping add a little more tolerance. The [JsonPropertyName] attributes below pin the exact wire names.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IChatClient _chat;
    private readonly IOptions<AiOptions> _options;
    private readonly ILogger<OllamaRecommendationEngine> _logger;

    /// <summary>Creates the engine over a chat client, the AI options, and a logger.</summary>
    /// <param name="chat">The provider-agnostic chat client (Ollama by default).</param>
    /// <param name="options">The AI options supplying the model, timeout, and output-token cap.</param>
    /// <param name="logger">The logger used for metrics-only structured events.</param>
    public OllamaRecommendationEngine(
        IChatClient chat,
        IOptions<AiOptions> options,
        ILogger<OllamaRecommendationEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _chat = chat;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RecommendationEngineResult> RankAsync(
        RecommendationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.Value;

        // Bound tail latency even inside a background job: a linked source cancels the call after the
        // configured per-request timeout while still honoring the caller's own cancellation (§8).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var messages = BuildMessages(request);

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    Temperature = DefaultTemperature,          // low → repeatable, on-taste (§8)
                    MaxOutputTokens = options.MaxOutputTokens,  // hard output cap (§8)
                    ResponseFormat = ChatResponseFormat.Json,   // ask for structured output; the shape is validated below
                },
                timeoutCts.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did NOT cancel, so this is our per-request timeout (or an inner transport timeout):
            // the model is effectively unavailable, so throw the typed failure the caller degrades on (ADR 0018).
            AiLog.RankTimedOut(_logger, exception, options.Model, options.TimeoutSeconds);
            throw new RecommendationEngineException(
                $"The recommendation model did not respond within {options.TimeoutSeconds}s.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Transport/protocol failure (model not running, connection refused, bad HTTP status). A
            // caller-initiated cancellation is NOT caught here and propagates as OperationCanceledException.
            AiLog.RankFailed(_logger, exception, options.Model);
            throw new RecommendationEngineException("The recommendation model was unavailable.", exception);
        }

        // Tolerant parse: a malformed or empty body is NOT an error — it degrades to an empty pick set so the
        // caller falls back (Milestone 5.2), mirroring the "degrade, never crash" posture of CachedTmdbClient.
        var picks = ParsePicks(response.Text, request, options.Model);
        var usage = new AiUsage
        {
            Model = options.Model,
            PromptTokens = ToTokenCount(response.Usage?.InputTokenCount),
            CompletionTokens = ToTokenCount(response.Usage?.OutputTokenCount),
        };

        if (picks.Count > 0)
        {
            AiLog.RankSucceeded(
                _logger, options.Model, picks.Count, request.Candidates.Count,
                usage.PromptTokens, usage.CompletionTokens);
        }

        return new RecommendationEngineResult { Picks = picks, Usage = usage };
    }

    // A system message (rules + required JSON shape) and one compact user message (the taste profile + the
    // numbered candidate list). Titles/years/genres/ratings only — no user id, display name, or email (§12).
    private static ChatMessage[] BuildMessages(RecommendationRequest request) =>
    [
        new(ChatRole.System, SystemPrompt),
        new(ChatRole.User, BuildUserPrompt(request)),
    ];

    private static string BuildUserPrompt(RecommendationRequest request)
    {
        var profile = request.Profile;
        var builder = new StringBuilder();

        builder.AppendLine("USER TASTE");

        if (profile.TopRated.Count > 0)
        {
            builder.AppendLine("Top-rated titles:");
            foreach (var rated in profile.TopRated)
            {
                builder.Append("- ").Append(rated.Title);
                AppendYear(builder, rated.Year);
                builder.Append(" — rated ").Append(rated.Rating).Append("/10");
                AppendGenres(builder, rated.Genres);
                builder.AppendLine();
            }
        }

        if (profile.WatchlistTitles.Count > 0)
        {
            builder.Append("Watchlist: ").AppendLine(string.Join(", ", profile.WatchlistTitles));
        }

        if (profile.FavoriteGenres.Count > 0)
        {
            builder.Append("Favorite genres: ").AppendLine(string.Join(", ", profile.FavoriteGenres));
        }

        builder.AppendLine();
        builder.AppendLine("CANDIDATES (choose only from these; identify each pick by its id):");

        var ordinal = 1;
        foreach (var candidate in request.Candidates)
        {
            builder.Append(ordinal).Append(". [id=").Append(candidate.TmdbId).Append("] ").Append(candidate.Title);
            AppendYear(builder, candidate.Year);
            AppendGenres(builder, candidate.Genres);
            builder.AppendLine();
            ordinal++;
        }

        return builder.ToString();
    }

    private static void AppendYear(StringBuilder builder, int? year)
    {
        if (year is int value)
        {
            builder.Append(" (").Append(value).Append(')');
        }
    }

    private static void AppendGenres(StringBuilder builder, IReadOnlyList<string> genres)
    {
        if (genres.Count > 0)
        {
            builder.Append(" — ").Append(string.Join(", ", genres));
        }
    }

    private List<RankedPick> ParsePicks(string? responseText, RecommendationRequest request, string model)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            AiLog.EmptyResponse(_logger, model);
            return [];
        }

        var envelope = TryDeserialize(responseText);
        if (envelope?.Picks is not { Count: > 0 })
        {
            AiLog.UnusableResponse(_logger, model, responseText.Length);
            return [];
        }

        // The candidate set is the source of truth for identity and metadata (ADR 0017): map the model's echoed
        // id back to the offered candidate to recover its media type, drop anything off-list (a hallucination),
        // and collapse duplicates. The authoritative hallucination guard re-checks this in Milestone 5.1.
        var candidatesById = BuildCandidateLookup(request.Candidates);
        var seen = new HashSet<(int TmdbId, MediaType Media)>();
        var picks = new List<RankedPick>(envelope.Picks.Count);

        foreach (var pick in envelope.Picks)
        {
            if (pick?.Id is not int id || !candidatesById.TryGetValue(id, out var candidate))
            {
                continue;
            }

            if (!seen.Add((candidate.TmdbId, candidate.Media)))
            {
                continue;
            }

            picks.Add(new RankedPick
            {
                TmdbId = candidate.TmdbId,
                Media = candidate.Media,
                Reason = CapReason(pick.Reason),
            });
        }

        return picks;
    }

    private static Dictionary<int, CandidateTitle> BuildCandidateLookup(IReadOnlyList<CandidateTitle> candidates)
    {
        var lookup = new Dictionary<int, CandidateTitle>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // The first candidate for a given TMDB id wins on the rare cross-media id collision; either way the
            // pick still resolves to a real offered candidate, so grounding holds.
            lookup.TryAdd(candidate.TmdbId, candidate);
        }

        return lookup;
    }

    private static PicksEnvelope? TryDeserialize(string responseText)
    {
        try
        {
            return JsonSerializer.Deserialize<PicksEnvelope>(responseText, JsonOptions);
        }
        catch (JsonException)
        {
            // Tolerant fallback: a small local model may wrap the JSON in prose or code fences. Try the first
            // brace-delimited object before giving up. Still never throws — a total failure degrades to empty.
            var extracted = TryExtractJsonObject(responseText);
            if (extracted is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<PicksEnvelope>(extracted, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static string? TryExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string CapReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        var trimmed = reason.Trim();
        return trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength];
    }

    // The model reports token counts as long?; clamp into the non-negative int the AiUsage ledger + the
    // AIRecommendationHistory columns store, treating a missing/negative count as 0.
    private static int ToTokenCount(long? value) =>
        value is > 0 ? (int)Math.Min(value.Value, int.MaxValue) : 0;

    // The response wire shape the system prompt asks the model to produce. Internal to the adapter — a vendor/
    // prompt DTO that never crosses into Application or Domain (ADR 0006/0016).
    private sealed record PicksEnvelope
    {
        [JsonPropertyName("picks")]
        public IReadOnlyList<PickDto>? Picks { get; init; }
    }

    private sealed record PickDto
    {
        [JsonPropertyName("id")]
        public int? Id { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
