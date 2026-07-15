namespace Cinora.Web.Infrastructure;

/// <summary>
/// Named rate-limiting policies applied to endpoints via <c>[EnableRateLimiting]</c> (Milestone 1.6b / F3).
/// Kept as compile-time constants so both the composition root (<c>Program.cs</c>, where the policy is
/// registered) and the controllers (where it is applied) reference one authoritative name — no magic
/// strings that can silently drift apart.
/// </summary>
public static class RateLimitingPolicies
{
    /// <summary>
    /// Throttles the unauthenticated authentication endpoints — <c>login</c>, <c>register</c> and the
    /// <c>external-login</c> challenge — to blunt credential stuffing and password brute-forcing. Fixed
    /// window partitioned per client IP so one abusive caller cannot lock everyone else out; strict in
    /// Production and relaxed to a very high permit in Development/Testing (configured in <c>Program.cs</c>)
    /// so the integration suite's repeated auth requests never trip it.
    /// </summary>
    public const string Auth = "auth";

    /// <summary>
    /// Throttles the anonymous, TMDB-proxying browse endpoints — the discovery rails and search
    /// (<c>Discovery.Rail</c>/<c>Search</c>/<c>SearchResults</c>). Each <em>distinct</em> query is a cache
    /// miss forwarded straight to TMDB, so an unauthenticated flood of unique terms would exhaust Cinora's
    /// shared TMDB budget (a search-DoS for real users) and emit unbounded outbound requests. Fixed window
    /// partitioned per client IP; strict in Production and relaxed to a very high permit in
    /// Development/Testing (configured in <c>Program.cs</c>) so the mock-first integration suite's repeated
    /// rail/search GETs never trip it. Standing rule (REVIEW_BACKLOG): every anonymous endpoint that proxies
    /// TMDB must carry this policy.
    /// </summary>
    public const string PublicRead = "public-read";

    /// <summary>
    /// Throttles the authenticated social WRITE endpoints — creating/editing/deleting a review (Milestone 3.1;
    /// likes/comments/friend requests join in 3.2/3.3). Blunts write-spam (flooding a title with reviews, or
    /// hammering edits). Fixed window partitioned per <em>user</em> (the authenticated actor) rather than per
    /// IP, so a spammer cannot dodge it by rotating IPs and users behind one NAT do not share a bucket; strict
    /// in Production and relaxed to a very high permit in Development/Testing (configured in <c>Program.cs</c>)
    /// so the integration suite's repeated writes never trip it. Design §10.4.
    /// </summary>
    public const string SocialWrite = "social-write";
}
