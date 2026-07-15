using Cinora.Application.Features.Recommendations;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Unit tests for <see cref="RecommendationSetEnvelope"/> — the compact <c>OutputSummary</c> serializer/parser
/// (Milestone 5.2, ADR 0018 §3). They prove the round-trip is lossless for every field a card needs (including
/// null poster/year and the <see cref="RecommendationSource"/> flag), the serializer keeps the JSON within the
/// 4000-char column budget by dropping trailing picks, and a null/garbage body parses as unusable so the serve
/// path can fall through rather than throw.
/// </summary>
public sealed class RecommendationSetEnvelopeTests
{
    [Fact]
    public void Serialize_then_TryParse_round_trips_all_card_fields_losslessly()
    {
        var original = new RecommendationSet
        {
            Source = RecommendationSource.Ai,
            GeneratedAtUtc = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc),
            Picks =
            [
                new RecommendationPick
                {
                    TmdbId = 27_205,
                    Media = MediaType.Movie,
                    Title = "Inception",
                    PosterPath = "/inception.jpg",
                    ReleaseYear = 2010,
                    Reason = "Because you rated Interstellar 9/10.",
                },
                new RecommendationPick
                {
                    TmdbId = 1_396,
                    Media = MediaType.Series,
                    Title = "Breaking Bad",
                    PosterPath = null,   // omitted from the envelope
                    ReleaseYear = null,  // omitted from the envelope
                    Reason = "Popular in Drama.",
                },
            ],
        };

        var json = RecommendationSetEnvelope.Serialize(original, AIRecommendationHistory.SummaryMaxLength);
        Assert.True(RecommendationSetEnvelope.TryParse(json, out var parsed));

        Assert.Equal(original.Source, parsed.Source);
        Assert.Equal(original.GeneratedAtUtc, parsed.GeneratedAtUtc);
        Assert.Equal(original.Picks.Count, parsed.Picks.Count);
        for (var i = 0; i < original.Picks.Count; i++)
        {
            Assert.Equal(original.Picks[i].TmdbId, parsed.Picks[i].TmdbId);
            Assert.Equal(original.Picks[i].Media, parsed.Picks[i].Media);
            Assert.Equal(original.Picks[i].Title, parsed.Picks[i].Title);
            Assert.Equal(original.Picks[i].PosterPath, parsed.Picks[i].PosterPath);
            Assert.Equal(original.Picks[i].ReleaseYear, parsed.Picks[i].ReleaseYear);
            Assert.Equal(original.Picks[i].Reason, parsed.Picks[i].Reason);
        }
    }

    [Fact]
    public void Serialize_then_TryParse_preserves_the_heuristic_source()
    {
        var original = new RecommendationSet
        {
            Source = RecommendationSource.Heuristic,
            GeneratedAtUtc = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            Picks = [new RecommendationPick { TmdbId = 1, Media = MediaType.Movie, Title = "X", Reason = "Popular." }],
        };

        var json = RecommendationSetEnvelope.Serialize(original, AIRecommendationHistory.SummaryMaxLength);
        Assert.True(RecommendationSetEnvelope.TryParse(json, out var parsed));
        Assert.Equal(RecommendationSource.Heuristic, parsed.Source);
    }

    [Fact]
    public void Serialize_drops_trailing_picks_to_fit_a_tight_budget_and_stays_parseable()
    {
        var set = new RecommendationSet
        {
            Source = RecommendationSource.Ai,
            GeneratedAtUtc = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            Picks = Enumerable.Range(1, 12)
                .Select(i => new RecommendationPick
                {
                    TmdbId = 900_000 + i,
                    Media = MediaType.Movie,
                    Title = $"A reasonably long candidate title number {i}",
                    PosterPath = $"/poster-{i}.jpg",
                    ReleaseYear = 2000 + i,
                    Reason = new string('r', 140), // the max reason length
                })
                .ToList(),
        };

        const int tightBudget = 400;
        var json = RecommendationSetEnvelope.Serialize(set, tightBudget);

        Assert.True(json.Length <= tightBudget);       // never overflows the column
        Assert.True(RecommendationSetEnvelope.TryParse(json, out var parsed));
        Assert.NotEmpty(parsed.Picks);                 // at least the leading picks survive
        Assert.True(parsed.Picks.Count < set.Picks.Count); // trailing picks were dropped to fit
        Assert.Equal(900_001, parsed.Picks[0].TmdbId); // leading picks are the ones retained, in order
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unexpected\":true}")] // valid JSON, wrong schema (no version) → unusable
    public void TryParse_returns_false_for_unusable_input(string? json)
    {
        Assert.False(RecommendationSetEnvelope.TryParse(json, out var parsed));
        Assert.Empty(parsed.Picks);
    }

    [Fact]
    public void TryParse_returns_false_when_a_pick_carries_an_undefined_media_value()
    {
        // A tampered/foreign OutputSummary: "m":7 is not a defined MediaType (Movie=0, Series=1). The whole
        // envelope must be treated as unusable — like the null-field rejects — so the serve path falls through to
        // the heuristic rather than serving a card routed to an undefined media.
        const string json =
            "{\"v\":1,\"src\":\"ai\",\"gen\":\"2026-07-06T00:00:00Z\"," +
            "\"picks\":[{\"t\":27205,\"m\":7,\"ti\":\"Inception\",\"r\":\"Because you liked it.\"}]}";

        Assert.False(RecommendationSetEnvelope.TryParse(json, out var parsed));
        Assert.Empty(parsed.Picks);
    }
}
