using System.Text.Json;
using System.Text.Json.Serialization;
using Cinora.Domain.Enums;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Serializes a <see cref="RecommendationSet"/> to (and parses it back from) the compact JSON envelope stored in
/// <c>AIRecommendationHistory.OutputSummary</c> (ADR 0018 §3, Phase 5 design §6). The envelope uses short
/// property names — <c>{"v":1,"src":"ai","gen":"&lt;iso8601&gt;","picks":[{"t":&lt;tmdbId&gt;,"m":0,"y":2014,
/// "ti":"Title","p":"/x.jpg","r":"reason"}]}</c> — so the row is <b>self-contained</b>: the serve fallback
/// (<c>GetMyRecommendationsQuery</c> tier b) rehydrates cards straight from it with NO TMDB or LLM call. The
/// round-trip is lossless for every field a card needs (<see cref="RecommendationPick.TmdbId"/>,
/// <see cref="RecommendationPick.Media"/>, <see cref="RecommendationPick.Title"/>,
/// <see cref="RecommendationPick.PosterPath"/>, <see cref="RecommendationPick.ReleaseYear"/>,
/// <see cref="RecommendationPick.Reason"/>, <see cref="RecommendationSet.GeneratedAtUtc"/>,
/// <see cref="RecommendationSet.Source"/>).
/// <para>
/// <see cref="Serialize"/> enforces a hard character budget (the 4000-char column) by dropping trailing picks
/// until the envelope fits — each retained pick stays lossless (design §6/§14 budget note); in the common case
/// all picks fit easily. <see cref="TryParse"/> returns <c>false</c> for a null/blank/corrupt/old-schema body so
/// the serve path falls through to the heuristic rather than throwing.
/// </para>
/// </summary>
public static class RecommendationSetEnvelope
{
    private const int SchemaVersion = 1;
    private const string AiSource = "ai";
    private const string HeuristicSource = "heu";

    // Explicit [JsonPropertyName]s pin the short wire names; WhenWritingNull omits absent year/poster to stay
    // compact within the 4000-char budget.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly RecommendationSet Empty = new()
    {
        Picks = [],
        Source = RecommendationSource.Ai,
        GeneratedAtUtc = default,
    };

    /// <summary>
    /// Serializes the set to the compact envelope, guaranteeing the result is at most
    /// <paramref name="maxLength"/> characters by dropping trailing picks if necessary (so it fits the
    /// <c>OutputSummary</c> column and <c>AIRecommendationHistory.Create</c>'s length guard).
    /// </summary>
    /// <param name="set">The served set to persist.</param>
    /// <param name="maxLength">The maximum character length of the produced JSON (the column cap).</param>
    /// <returns>The compact, budget-bounded envelope JSON.</returns>
    public static string Serialize(RecommendationSet set, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(set);

        var pickDtos = set.Picks.Select(ToDto).ToList();
        var envelope = new Envelope
        {
            Version = SchemaVersion,
            Source = set.Source == RecommendationSource.Heuristic ? HeuristicSource : AiSource,
            GeneratedAtUtc = set.GeneratedAtUtc,
            Picks = pickDtos,
        };

        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        // Budget guard: the envelope references pickDtos by reference, so trimming that list and re-serializing
        // shrinks the JSON. Drop trailing picks until it fits; each retained pick stays fully lossless.
        while (json.Length > maxLength && pickDtos.Count > 0)
        {
            pickDtos.RemoveAt(pickDtos.Count - 1);
            json = JsonSerializer.Serialize(envelope, JsonOptions);
        }

        return json;
    }

    /// <summary>
    /// Parses the envelope back into a <see cref="RecommendationSet"/>. Returns <c>false</c> (and an empty set)
    /// for a null/blank body, invalid JSON, an unknown schema version, or a pick missing a required field — the
    /// serve path treats any of these as "no usable history" and degrades to the heuristic.
    /// </summary>
    /// <param name="json">The stored <c>OutputSummary</c> envelope.</param>
    /// <param name="set">The parsed set on success; an empty set otherwise.</param>
    /// <returns><c>true</c> when the envelope parsed into a usable set; otherwise <c>false</c>.</returns>
    public static bool TryParse(string? json, out RecommendationSet set)
    {
        set = Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null || envelope.Version != SchemaVersion || envelope.Picks is null)
        {
            return false;
        }

        var picks = new List<RecommendationPick>(envelope.Picks.Count);
        foreach (var dto in envelope.Picks)
        {
            if (dto?.Title is null || dto.Reason is null)
            {
                return false; // a corrupt pick → treat the whole row as unusable, fall through to the heuristic
            }

            if (!Enum.IsDefined((MediaType)dto.Media))
            {
                return false; // a tampered/foreign media value → unusable, so the served card never routes wrong
            }

            picks.Add(new RecommendationPick
            {
                TmdbId = dto.TmdbId,
                Media = (MediaType)dto.Media,
                Title = dto.Title,
                PosterPath = dto.PosterPath,
                ReleaseYear = dto.Year,
                Reason = dto.Reason,
            });
        }

        set = new RecommendationSet
        {
            Picks = picks,
            Source = string.Equals(envelope.Source, HeuristicSource, StringComparison.Ordinal)
                ? RecommendationSource.Heuristic
                : RecommendationSource.Ai,
            GeneratedAtUtc = envelope.GeneratedAtUtc,
        };
        return true;
    }

    private static PickDto ToDto(RecommendationPick pick) =>
        new()
        {
            TmdbId = pick.TmdbId,
            Media = (int)pick.Media,
            Year = pick.ReleaseYear,
            Title = pick.Title,
            PosterPath = pick.PosterPath,
            Reason = pick.Reason,
        };

    // The persisted wire shape. Short names keep the row inside the 4000-char column; a version field lets a
    // future schema change be detected (an unknown version parses as unusable → heuristic fallback).
    private sealed record Envelope
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("src")]
        public string? Source { get; init; }

        [JsonPropertyName("gen")]
        public DateTime GeneratedAtUtc { get; init; }

        [JsonPropertyName("picks")]
        public List<PickDto>? Picks { get; init; }
    }

    private sealed record PickDto
    {
        [JsonPropertyName("t")]
        public int TmdbId { get; init; }

        [JsonPropertyName("m")]
        public int Media { get; init; }

        [JsonPropertyName("y")]
        public int? Year { get; init; }

        [JsonPropertyName("ti")]
        public string? Title { get; init; }

        [JsonPropertyName("p")]
        public string? PosterPath { get; init; }

        [JsonPropertyName("r")]
        public string? Reason { get; init; }
    }
}
