using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Recommendations;

/// <summary>
/// The serve path for the current user's recommendations (ADR 0018 §4, Phase 5 design §7.2). A <b>query</b>:
/// owner-implicit (a user only ever reads their OWN set, resolved server-side from <c>ICurrentUser</c> — no id is
/// bound), it NEVER writes and NEVER calls the LLM. It is the only thing the For-You surfaces dispatch, and it is
/// the exit-criterion handler — "page load makes no LLM call."
/// </summary>
public sealed record GetMyRecommendationsQuery : IRequest<RecommendationSet>;

/// <summary>
/// Handles <see cref="GetMyRecommendationsQuery"/> with the three-tier, LLM-free serve order (ADR 0018 §4):
/// <list type="number">
///   <item><b>(a) served cache hit</b> → return it (<see cref="RecommendationSource.Ai"/>), the steady-state fast
///   path;</item>
///   <item><b>(b) cache miss</b> → the latest <c>AIRecommendationHistory</c> row (indexed <c>Top(1)</c> over
///   <c>(UserId, GeneratedAtUtc)</c>), parsed from its self-contained <c>OutputSummary</c> envelope
///   (<see cref="RecommendationSource.Ai"/>). This is a READ ONLY — a query never re-warms the cache (a write);
///   the next precompute re-warms it. A corrupt/old-schema envelope falls through, it never throws;</item>
///   <item><b>(c) no usable history</b> → the deterministic <see cref="IHeuristicRecommender"/>
///   (<see cref="RecommendationSource.Heuristic"/>) so the page always renders.</item>
/// </list>
/// The handler has NO <see cref="IRecommendationEngine"/> dependency by design (the zero-page-load-LLM contract).
/// The cache read is additionally wrapped defensively: the adapter already degrades internally, but "never 500 on
/// serve" is a hard exit criterion, so even a throwing cache falls through to history/heuristic.
/// </summary>
public sealed class GetMyRecommendationsQueryHandler(
    ICurrentUser currentUser,
    IServedRecommendationCache servedCache,
    IAppDbContext db,
    IHeuristicRecommender heuristic,
    ILogger<GetMyRecommendationsQueryHandler> logger)
    : IRequestHandler<GetMyRecommendationsQuery, RecommendationSet>
{
    /// <inheritdoc />
    public async Task<RecommendationSet> Handle(GetMyRecommendationsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        // (a) Served cache hit — the steady-state fast path. Defensive try/catch: the adapter degrades internally
        // and should never throw, but the serve path is a hard "never 500" exit criterion, so a cache fault still
        // falls through to the durable fallback rather than surfacing.
        try
        {
            var cached = await servedCache.GetAsync(userId, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecommendationsLog.ServeCacheReadFailed(logger, exception, userId);
        }

        // (b) Cache miss → the latest history row, parsed from its self-contained envelope. NO cache re-warm here
        // (that is a write — forbidden in a query). A blank/corrupt/old-schema envelope falls through to (c).
        var latestSummary = await db.AIRecommendationHistories.AsNoTracking()
            .Where(history => history.UserId == userId)
            .OrderByDescending(history => history.GeneratedAtUtc)
            .Select(history => history.OutputSummary)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSummary is not null)
        {
            if (RecommendationSetEnvelope.TryParse(latestSummary, out var fromHistory))
            {
                return fromHistory;
            }

            RecommendationsLog.HistoryParseFailed(logger, userId);
        }

        // (c) No usable history → the deterministic, LLM-free heuristic. Always returns a set so the page renders.
        return await heuristic.RecommendAsync(userId, cancellationToken);
    }
}
