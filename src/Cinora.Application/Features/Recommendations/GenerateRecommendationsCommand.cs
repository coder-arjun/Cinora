using System.Text;
using Cinora.Application.Common.Ai;
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// Generates a grounded, explained recommendation set for one user (ADR 0017/0018, Phase 5 design §2/§5.4/§6). A
/// <b>command</b> — the pipeline's only writer: it persists one <c>AIRecommendationHistory</c> audit row and
/// warms the served cache on a successful, non-empty AI set. Dispatched off the request path by the precompute
/// job (Milestone 5.3), never on a page render, so the LLM is never called synchronously in a request. The target
/// <c>userId</c> is an explicit argument (the job supplies it) — this command is not owner-resolved.
/// </summary>
/// <param name="UserId">The user to generate recommendations for.</param>
public sealed record GenerateRecommendationsCommand(Guid UserId) : IRequest<RecommendationSet>;

/// <summary>
/// Handles <see cref="GenerateRecommendationsCommand"/>: taste profile → grounded candidates → engine ranking →
/// the <b>authoritative hallucination guard</b> → persist + warm → the served set (ADR 0017 §3, ADR 0018 §3). The
/// guard is the correctness core: a pick is kept only if its <c>(TmdbId, Media)</c> is in the offered candidate
/// set; off-list picks are dropped (logged), duplicates collapse, and the kept set is capped to <c>MaxResults</c>.
/// Card metadata comes from the candidate; only the reason comes from the model (trimmed, surrogate-safe).
/// <para>
/// On a successful, non-empty set the handler writes EXACTLY ONE <c>AIRecommendationHistory</c> row (the model +
/// token usage from <see cref="AiUsage"/>, a PII-free <c>InputSummary</c> of the grounding, and a self-contained
/// <c>OutputSummary</c> envelope of the served set) then warms <see cref="IServedRecommendationCache"/>. A thin
/// profile, an empty candidate pool, an engine outage, or zero grounded picks all degrade to an empty set and
/// persist NOTHING and warm NOTHING (leaving any prior cache/history intact) — the serve path degrades
/// independently. Skip-unchanged dedup — the 5.3 precompute job skipping a user whose latest
/// <c>AIRecommendationHistory.GeneratedAtUtc</c> is newer than their latest review/watchlist activity
/// (timestamp-based, not a taste hash; ADR 0018 amendment) — is deliberately a Milestone 5.3 (job) concern, not here.
/// </para>
/// </summary>
public sealed class GenerateRecommendationsCommandHandler(
    IUserTasteProfileBuilder tasteProfileBuilder,
    IRecommendationCandidateSource candidateSource,
    IRecommendationEngine engine,
    IRecommendationPolicy policy,
    IAppDbContext db,
    IServedRecommendationCache servedCache,
    TimeProvider timeProvider,
    ILogger<GenerateRecommendationsCommandHandler> logger)
    : IRequestHandler<GenerateRecommendationsCommand, RecommendationSet>
{
    /// <inheritdoc />
    public async Task<RecommendationSet> Handle(GenerateRecommendationsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) Taste profile. A cold-start user (no reviews, no watchlist) skips the model entirely (§5.1).
        var taste = await tasteProfileBuilder.BuildAsync(request.UserId, cancellationToken);
        if (!taste.HasSignal)
        {
            RecommendationsLog.SkippedThinProfile(logger, request.UserId);
            return Empty();
        }

        // 2) Grounded candidates. No candidates → nothing to rank; skip the model.
        var candidates = await candidateSource.GenerateAsync(taste, cancellationToken);
        if (candidates.Count == 0)
        {
            RecommendationsLog.SkippedNoCandidates(logger, request.UserId);
            return Empty();
        }

        // 3) Rank + explain. A model outage degrades to an empty set (the serve path recovers in 5.2) — never rethrow.
        RecommendationEngineResult result;
        try
        {
            result = await engine.RankAsync(
                new RecommendationRequest { Profile = taste.Profile, Candidates = candidates },
                cancellationToken);
        }
        catch (RecommendationEngineException exception)
        {
            RecommendationsLog.EngineUnavailable(logger, exception, request.UserId);
            return Empty();
        }

        // 4) The authoritative hallucination guard.
        var picks = ApplyHallucinationGuard(result, candidates, request.UserId);
        if (picks.Count == 0)
        {
            // Zero grounded picks (e.g. the model hallucinated everything) is a generation miss, not a set.
            RecommendationsLog.NoGroundedPicks(logger, request.UserId, result.Picks.Count);
            return Empty();
        }

        // 5) Build the served set, then persist ONE audit row and warm the served cache (ADR 0018 §3). Only a
        //    successful, non-empty AI set reaches here; every empty path above persisted/warmed nothing.
        var set = new RecommendationSet
        {
            Picks = picks,
            Source = RecommendationSource.Ai,
            GeneratedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        var history = AIRecommendationHistory.Create(
            request.UserId,
            result.Usage.Model,
            BuildInputSummary(taste, candidates.Count),
            RecommendationSetEnvelope.Serialize(set, AIRecommendationHistory.SummaryMaxLength),
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens);

        db.AIRecommendationHistories.Add(history);
        await db.SaveChangesAsync(cancellationToken);

        // Warm the served set (best-effort inside the adapter — a cache outage never throws; ADR 0018 §2). The
        // durable history row is already persisted, so a warm failure never loses the generation.
        await servedCache.SetAsync(request.UserId, set, cancellationToken);

        RecommendationsLog.GenerationPersisted(
            logger, request.UserId, picks.Count, result.Usage.PromptTokens, result.Usage.CompletionTokens);

        return set;
    }

    // A compact, PII-free audit of the grounding inputs (titles/genres/ratings + candidate count only — never the
    // user id/name/email; §12), bounded to the OutputSummary column so AIRecommendationHistory.Create's length
    // guard never rejects it. Always non-empty (the candidate count is always appended).
    private static string BuildInputSummary(UserTasteResult taste, int candidateCount)
    {
        var profile = taste.Profile;
        var builder = new StringBuilder();

        if (profile.TopRated.Count > 0)
        {
            builder.Append("Top-rated: ").AppendJoin("; ", profile.TopRated.Select(FormatRated)).Append(". ");
        }

        if (profile.FavoriteGenres.Count > 0)
        {
            builder.Append("Favorite genres: ").AppendJoin(", ", profile.FavoriteGenres).Append(". ");
        }

        builder.Append("Candidates: ").Append(candidateCount).Append('.');

        return RecommendationText.Truncate(builder.ToString(), AIRecommendationHistory.SummaryMaxLength);
    }

    private static string FormatRated(RatedTitle rated) =>
        rated.Year is int year
            ? $"{rated.Title} ({year}) {rated.Rating}/10"
            : $"{rated.Title} {rated.Rating}/10";

    // Keeps only picks whose (TmdbId, Media) is in the offered candidate set, drops off-list picks (logged),
    // collapses duplicates, and caps to MaxResults. Card metadata comes from the candidate; only the reason
    // comes from the model (trimmed, surrogate-safe).
    private List<RecommendationPick> ApplyHallucinationGuard(
        RecommendationEngineResult result, IReadOnlyList<CandidateTitle> candidates, Guid userId)
    {
        var candidateByKey = new Dictionary<(int TmdbId, MediaType Media), CandidateTitle>();
        foreach (var candidate in candidates)
        {
            candidateByKey[(candidate.TmdbId, candidate.Media)] = candidate;
        }

        var kept = new List<RecommendationPick>();
        var keptKeys = new HashSet<(int TmdbId, MediaType Media)>();
        var droppedOffList = 0;

        foreach (var pick in result.Picks)
        {
            var key = (pick.TmdbId, pick.Media);
            if (!candidateByKey.TryGetValue(key, out var candidate))
            {
                droppedOffList++; // a hallucinated or off-list id — never served
                continue;
            }

            if (kept.Count >= policy.MaxResults || !keptKeys.Add(key))
            {
                continue; // capped, or a duplicate of a pick already kept
            }

            kept.Add(new RecommendationPick
            {
                TmdbId = candidate.TmdbId,
                Media = candidate.Media,
                Title = candidate.Title,
                PosterPath = candidate.PosterPath,
                ReleaseYear = candidate.Year,
                Reason = RecommendationText.Truncate(pick.Reason, policy.MaxReasonLength),
            });
        }

        if (droppedOffList > 0)
        {
            RecommendationsLog.DroppedOffListPicks(logger, droppedOffList, userId);
        }

        return kept;
    }

    private RecommendationSet Empty() =>
        new()
        {
            Picks = [],
            Source = RecommendationSource.Ai,
            GeneratedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
}
