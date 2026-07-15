using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Exceptions;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Ai;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Infrastructure.Tests.Ai;

/// <summary>
/// Verifies the <see cref="OllamaRecommendationEngine"/> adapter against a hand-rolled <see cref="FakeChatClient"/>
/// (no live Ollama): it builds a PII-free prompt from the taste profile + candidate set, requests structured
/// JSON at a low temperature under a bounded output cap, parses a well-formed body into ranked picks (recovering
/// each pick's media from the offered candidate) with the model's reported token usage, drops off-list/duplicate
/// picks, tolerantly degrades a malformed/empty body to an empty result WITHOUT throwing, and throws
/// <see cref="RecommendationEngineException"/> only on a genuine transport failure or the per-request timeout.
/// </summary>
public sealed class OllamaRecommendationEngineTests
{
    // Microsoft.Extensions.Options.Options is fully qualified because the Cinora.Infrastructure.Options
    // namespace (source of AiOptions) otherwise shadows the framework's Options helper type.
    private static OllamaRecommendationEngine CreateSut(IChatClient chat, AiOptions? options = null) =>
        new(chat,
            Microsoft.Extensions.Options.Options.Create(options ?? new AiOptions()),
            NullLogger<OllamaRecommendationEngine>.Instance);

    private static RecommendationRequest Request() => new()
    {
        Profile = new TasteProfile
        {
            TopRated = [new RatedTitle { Title = "Inception", Year = 2010, Rating = 9, Genres = ["Action", "Sci-Fi"] }],
            WatchlistTitles = ["Dune"],
            FavoriteGenres = ["Sci-Fi", "Thriller"],
        },
        Candidates =
        [
            new CandidateTitle
            {
                TmdbId = 693134, Media = MediaType.Movie, Title = "Dune: Part Two", Year = 2024,
                Genres = ["Sci-Fi", "Adventure"],
            },
            new CandidateTitle
            {
                TmdbId = 1399, Media = MediaType.Series, Title = "Game of Thrones", Year = 2011,
                Genres = ["Drama", "Fantasy"],
            },
        ],
    };

    // Builds a canned assistant response carrying the given text and reported token usage.
    private static ChatResponse Response(string text, long input = 11, long output = 22) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    private static FakeChatClient Returns(string body) =>
        new((_, _) => Task.FromResult(Response(body)));

    // ---- Prompt building + hygiene + model options ----

    [Fact]
    public async Task RankAsync_builds_prompt_from_taste_and_candidates_without_leaking_identity()
    {
        var fake = Returns("{\"picks\":[]}");
        var sut = CreateSut(fake);

        await sut.RankAsync(Request(), CancellationToken.None);

        Assert.Equal(2, fake.LastMessages.Count);
        Assert.Equal(ChatRole.System, fake.LastMessages[0].Role);
        Assert.Equal(ChatRole.User, fake.LastMessages[1].Role);

        var userPrompt = fake.LastMessages[1].Text;
        // The profile (titles, ratings, genres) and the numbered candidate list (with ids) are present...
        Assert.Contains("Inception", userPrompt, StringComparison.Ordinal);
        Assert.Contains("9/10", userPrompt, StringComparison.Ordinal);
        Assert.Contains("Sci-Fi", userPrompt, StringComparison.Ordinal);
        Assert.Contains("Dune: Part Two", userPrompt, StringComparison.Ordinal);
        Assert.Contains("id=693134", userPrompt, StringComparison.Ordinal);
        // ...and nothing that could be user identity: the RecommendationRequest carries no id/name/email at all,
        // so the adapter structurally cannot leak PII. Assert the absence of an email marker as a guard.
        Assert.DoesNotContain("@", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankAsync_requests_structured_json_at_low_temperature_within_the_output_cap()
    {
        var fake = Returns("{\"picks\":[]}");
        var sut = CreateSut(fake);

        await sut.RankAsync(Request(), CancellationToken.None);

        Assert.Equal(ChatResponseFormat.Json, fake.LastOptions?.ResponseFormat);
        Assert.Equal(0.3f, fake.LastOptions?.Temperature);
        Assert.Equal(new AiOptions().MaxOutputTokens, fake.LastOptions?.MaxOutputTokens);
    }

    // ---- Well-formed parsing ----

    [Fact]
    public async Task RankAsync_parses_wellformed_json_into_ranked_picks_and_usage()
    {
        const string json =
            "{\"picks\":[" +
            "{\"id\":1399,\"reason\":\"Epic fantasy like your favorites.\"}," +
            "{\"id\":693134,\"reason\":\"More sci-fi you will love.\"}]}";
        var fake = new FakeChatClient((_, _) => Task.FromResult(Response(json, input: 120, output: 45)));
        var sut = CreateSut(fake);

        var result = await sut.RankAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.Picks.Count);
        // Order is preserved (best match first, as the model returned).
        Assert.Equal(1399, result.Picks[0].TmdbId);
        Assert.Equal(MediaType.Series, result.Picks[0].Media);   // media recovered from the candidate, not the model
        Assert.Equal("Epic fantasy like your favorites.", result.Picks[0].Reason);
        Assert.Equal(693134, result.Picks[1].TmdbId);
        Assert.Equal(MediaType.Movie, result.Picks[1].Media);

        Assert.Equal(new AiOptions().Model, result.Usage.Model);
        Assert.Equal(120, result.Usage.PromptTokens);
        Assert.Equal(45, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task RankAsync_tolerantly_parses_json_wrapped_in_prose_or_code_fences()
    {
        const string body =
            "Sure! Here are the picks:\n```json\n{\"picks\":[{\"id\":693134,\"reason\":\"fenced\"}]}\n```\nHope that helps.";
        var sut = CreateSut(Returns(body));

        var result = await sut.RankAsync(Request(), CancellationToken.None);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(693134, pick.TmdbId);
        Assert.Equal("fenced", pick.Reason);
    }

    // ---- Grounding within the adapter ----

    [Fact]
    public async Task RankAsync_drops_picks_that_are_not_in_the_candidate_set()
    {
        const string json =
            "{\"picks\":[{\"id\":999999,\"reason\":\"hallucinated\"},{\"id\":693134,\"reason\":\"grounded\"}]}";
        var sut = CreateSut(Returns(json));

        var result = await sut.RankAsync(Request(), CancellationToken.None);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(693134, pick.TmdbId);
        Assert.Equal("grounded", pick.Reason);
    }

    [Fact]
    public async Task RankAsync_collapses_duplicate_picks_keeping_the_first()
    {
        const string json =
            "{\"picks\":[{\"id\":693134,\"reason\":\"first\"},{\"id\":693134,\"reason\":\"again\"}]}";
        var sut = CreateSut(Returns(json));

        var result = await sut.RankAsync(Request(), CancellationToken.None);

        var pick = Assert.Single(result.Picks);
        Assert.Equal("first", pick.Reason);
    }

    // ---- Degrade-not-crash on unusable output ----

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{ \"picks\": [ { \"id\":")]   // truncated / no closing brace
    [InlineData("{\"picks\":null}")]           // well-formed JSON, no usable picks
    [InlineData("{\"picks\":[]}")]             // empty pick list
    [InlineData("")]
    [InlineData("   ")]
    public async Task RankAsync_returns_empty_result_and_does_not_throw_on_unusable_output(string body)
    {
        var sut = CreateSut(Returns(body));

        var result = await sut.RankAsync(Request(), CancellationToken.None);

        Assert.Empty(result.Picks);
        // Not an exception: usage is still reported (0 counts here) so the caller degrades, it never crashes.
        Assert.Equal(new AiOptions().Model, result.Usage.Model);
    }

    // ---- Failure postures ----

    [Fact]
    public async Task RankAsync_wraps_a_transport_failure_in_RecommendationEngineException()
    {
        var boom = new InvalidOperationException("connection refused");
        var fake = new FakeChatClient((_, _) => Task.FromException<ChatResponse>(boom));
        var sut = CreateSut(fake);

        var exception = await Assert.ThrowsAsync<RecommendationEngineException>(
            () => sut.RankAsync(Request(), CancellationToken.None));
        Assert.Same(boom, exception.InnerException);
    }

    [Fact]
    public async Task RankAsync_times_out_and_throws_RecommendationEngineException()
    {
        // The fake blocks until the adapter's linked timeout cancels it; TimeoutSeconds = 1 keeps the test quick.
        var fake = new FakeChatClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response("{\"picks\":[]}");
        });
        var sut = CreateSut(fake, new AiOptions { TimeoutSeconds = 1 });

        await Assert.ThrowsAsync<RecommendationEngineException>(
            () => sut.RankAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task RankAsync_propagates_caller_cancellation_as_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeChatClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response("{\"picks\":[]}");
        });
        var sut = CreateSut(fake);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.RankAsync(Request(), cts.Token));
    }
}
