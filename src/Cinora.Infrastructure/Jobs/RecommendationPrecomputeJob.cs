using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Recommendations;
using Cinora.Infrastructure.Options;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Jobs;

/// <summary>
/// The nightly Hangfire recurring job that precomputes AI recommendations OFF the request path (Phase 5 design
/// §10, ADR 0018 §5 — redeeming ADR 0007's Hangfire deferral). It is the pipeline's <b>orchestrator</b>: it
/// selects the users whose taste has changed since their last generation and dispatches
/// <see cref="GenerateRecommendationsCommand"/> for each via <see cref="ISender"/> (the ADR-0008 "job/controller
/// orchestrates, handler stays pure" pattern — never <see cref="ISender"/> inside a handler). It reads no
/// <c>ICurrentUser</c> (a background job has no HTTP context); the target user is always an explicit
/// <see cref="Guid"/> passed to the command.
/// <para>
/// <b>Idempotent + safe to re-run — within an accurate contract.</b> A user is <em>stale</em> (regenerated) iff
/// they have review/watchlist activity <b>within the lookback window</b> (<see cref="RecommendationOptions.PrecomputeLookbackDays"/>)
/// AND (no history row OR their latest history predates their latest activity). A user with a <b>prior successful
/// generation</b> newer than that activity is skipped on the next run (a history row now newer than their
/// activity), so unchanged users cost no duplicate work and gain no extra row. Note the honest edge: a generation
/// that yields an <b>empty</b> set — a thin profile, no candidates, an engine/TMDB outage, or an all-hallucinated
/// result — writes <em>no</em> <c>AIRecommendationHistory</c> row (that table is the successful-AI audit trail), so
/// such a user stays "stale" and is <em>re-attempted on subsequent runs while still inside the lookback window</em>.
/// That re-attempt is desirable (it retries a transient engine/TMDB outage) and self-bounds because the user ages
/// out of the window. A durable taste-hash / attempt marker is the recorded future refinement if persistently-empty
/// re-attempts ever matter at scale (REVIEW_BACKLOG). <c>[DisableConcurrentExecution]</c> guards overlap and
/// per-user failures are caught so one bad user never aborts the batch. All timestamps are UTC.
/// </para>
/// </summary>
[DisableConcurrentExecution(LockTimeoutSeconds)]
[AutomaticRetry(Attempts = MaxRetryAttempts)]
internal sealed class RecommendationPrecomputeJob(
    IServiceScopeFactory scopeFactory,
    IOptions<RecommendationOptions> options,
    TimeProvider timeProvider,
    ILogger<RecommendationPrecomputeJob> logger)
{
    // [DisableConcurrentExecution] holds a distributed lock for the ENTIRE RunAsync duration so two scheduled runs
    // never overlap. This constant is the lock-ACQUISITION-WAIT timeout — how long a second run blocks trying to
    // take the lock before Hangfire gives up — NOT a cap on how long the lock is held. The batch may run longer
    // than 300s and keeps the lock the whole time (each user is generated in its own fresh scope).
    private const int LockTimeoutSeconds = 300;

    // Bounded retries: the batch is idempotent, so a transient failure (e.g. a DB blip) is worth retrying a few
    // times, but not the Hangfire default of 10 (the nightly cadence makes an eventual next run cheap anyway).
    private const int MaxRetryAttempts = 3;

    /// <summary>
    /// Runs one precompute batch: select the stale users (a bounded, set-based read), then dispatch a generation
    /// for each in a fresh DI scope so every command gets a clean <c>DbContext</c>. Honors the cancellation token
    /// between users; a per-user failure is logged and the batch continues.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation between users.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrecomputeLog.BatchStarted(logger);

        // Only users active within this lookback window are considered (Phase 5 design §10) — this bounds both the
        // set-based selection reads and the in-memory selection dictionaries. NOTE (see the type doc for the full
        // contract): a user whose generation yields an EMPTY set writes no history row, so they remain "stale" and
        // are re-attempted on later runs while still inside the window — a desirable transient-outage retry that
        // self-bounds. A durable taste-hash/attempt marker is the future refinement if persistently-empty
        // re-attempts ever matter at scale (REVIEW_BACKLOG). timeProvider (not DateTime.UtcNow) so tests are
        // deterministic and the cutoff is testable.
        var cutoff = timeProvider.GetUtcNow().UtcDateTime
            - TimeSpan.FromDays(options.Value.PrecomputeLookbackDays);

        StaleUserSelection selection;

        // One scope for the read-only selection query; the per-user generations each get their own below.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            selection = await SelectStaleUsersAsync(db, cutoff, cancellationToken);
        }

        PrecomputeLog.UsersSelected(logger, selection.StaleUsers.Count, selection.SkippedUnchanged);

        var generated = 0;
        var failed = 0;

        foreach (var userId in selection.StaleUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A FRESH scope per user: each GenerateRecommendationsCommand gets a clean, isolated DbContext, and
            // one user's failure cannot corrupt another's unit of work.
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            try
            {
                await sender.Send(new GenerateRecommendationsCommand(userId), cancellationToken);
                generated++;
                PrecomputeLog.UserDispatched(logger, userId);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A per-user OperationCanceledException that is NOT the batch/shutdown token (e.g. an engine
                // per-request timeout surfacing as a raw OCE) is a per-user failure — record it and continue. A
                // genuine batch-token cancellation fails this `when` guard, is not caught here, and propagates out
                // of the loop to abort the batch (ADR 0018 §5).
                failed++;
                PrecomputeLog.UserFailed(logger, exception, userId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad user must not abort the batch (ADR 0018 §5). A batch-token cancellation, by contrast, is
                // excluded here (and by the guard above) and propagates.
                failed++;
                PrecomputeLog.UserFailed(logger, exception, userId);
            }
        }

        PrecomputeLog.BatchCompleted(logger, selection.StaleUsers.Count, selection.SkippedUnchanged, generated, failed);
    }

    // Selects the users whose taste has changed since their last generation, using three bounded, set-based
    // AsNoTracking reads (no N+1): per-user latest review touch, per-user latest watchlist touch, and per-user
    // latest generation. Indexes (UserId, CreatedAtUtc) / (UserId, AddedAtUtc) / (UserId, GeneratedAtUtc) serve
    // these. The two ACTIVITY reads are filtered to the lookback window (rows created/added OR updated on/after
    // cutoff) so only recently-active users are scanned — the history read is deliberately NOT filtered (a user's
    // latest generation is still compared for freshness). A user is stale iff they have in-window activity AND
    // (no history OR history is older than the activity).
    private static async Task<StaleUserSelection> SelectStaleUsersAsync(
        IAppDbContext db, DateTime cutoff, CancellationToken cancellationToken)
    {
        // Per-user latest review activity within the lookback window: MAX over CreatedAtUtc and the (nullable)
        // UpdatedAtUtc. Two separate aggregates (rather than a coalesce inside MAX) keep the SQL translation
        // trivial and provider-agnostic; the Where runs server-side before the grouping so out-of-window-only
        // users never materialize.
        var reviewActivity = await db.Reviews.AsNoTracking()
            .Where(review => review.CreatedAtUtc >= cutoff
                || (review.UpdatedAtUtc != null && review.UpdatedAtUtc >= cutoff))
            .GroupBy(review => review.UserId)
            .Select(group => new UserActivity(
                group.Key,
                group.Max(review => review.CreatedAtUtc),
                group.Max(review => review.UpdatedAtUtc)))
            .ToListAsync(cancellationToken);

        // Per-user latest watchlist activity within the lookback window: MAX over AddedAtUtc and the (nullable)
        // UpdatedAtUtc.
        var watchlistActivity = await db.Watchlists.AsNoTracking()
            .Where(entry => entry.AddedAtUtc >= cutoff
                || (entry.UpdatedAtUtc != null && entry.UpdatedAtUtc >= cutoff))
            .GroupBy(entry => entry.UserId)
            .Select(group => new UserActivity(
                group.Key,
                group.Max(entry => entry.AddedAtUtc),
                group.Max(entry => entry.UpdatedAtUtc)))
            .ToListAsync(cancellationToken);

        // Per-user latest generation instant (NOT lookback-filtered — see the method summary).
        var historyActivity = await db.AIRecommendationHistories.AsNoTracking()
            .GroupBy(history => history.UserId)
            .Select(group => new { UserId = group.Key, Latest = group.Max(history => history.GeneratedAtUtc) })
            .ToListAsync(cancellationToken);

        var latestActivity = new Dictionary<Guid, DateTime>();

        foreach (var activity in reviewActivity)
        {
            Accumulate(latestActivity, activity.UserId, activity.HighWaterMark);
        }

        foreach (var activity in watchlistActivity)
        {
            Accumulate(latestActivity, activity.UserId, activity.HighWaterMark);
        }

        var latestHistory = historyActivity.ToDictionary(item => item.UserId, item => item.Latest);

        var staleUsers = new List<Guid>();
        var skippedUnchanged = 0;

        foreach (var (userId, activityAt) in latestActivity)
        {
            // Fresh iff a history row is at least as new as the latest activity; otherwise regenerate. A user with
            // no history row is never in latestHistory, so they are always stale (given they have activity).
            if (latestHistory.TryGetValue(userId, out var historyAt) && historyAt >= activityAt)
            {
                skippedUnchanged++;
                continue;
            }

            staleUsers.Add(userId);
        }

        return new StaleUserSelection(staleUsers, skippedUnchanged);
    }

    private static void Accumulate(Dictionary<Guid, DateTime> latest, Guid userId, DateTime instant)
    {
        latest[userId] = latest.TryGetValue(userId, out var existing) && existing >= instant ? existing : instant;
    }

    // The per-user activity high-water mark from one source: the later of the create/add instant and the
    // (nullable) update instant. UpdatedAtUtc is only ever set on an edit that happens after the create, but the
    // two MAX aggregates may come from different rows, so take the later of the two.
    private readonly record struct UserActivity(Guid UserId, DateTime Created, DateTime? Updated)
    {
        public DateTime HighWaterMark => Updated is DateTime updated && updated > Created ? updated : Created;
    }

    private sealed record StaleUserSelection(IReadOnlyList<Guid> StaleUsers, int SkippedUnchanged);
}
