namespace Cinora.Web.Infrastructure;

/// <summary>
/// Sets Cinora's deliberate PWA cache policy on HTML responses so the service worker (Milestone 6.1,
/// ADR 0019) caches exactly the right pages:
/// <list type="bullet">
///   <item><b>Authenticated HTML → <c>Cache-Control: no-store</c></b> (private). The SW refuses to cache a
///   <c>no-store</c> response, so one user's personalized pages (<c>/home</c> feed, <c>/notifications</c>,
///   <c>/settings</c>, <c>/friends</c>, <c>/watchlist</c>) are never stored in the shared SW cache where a
///   second user on a shared device could read them offline.</item>
///   <item><b>Anonymous public GET HTML → <c>Cache-Control: no-cache</c></b> (cacheable, revalidate). The
///   framework stamps <c>no-cache, no-store</c> on <i>every</i> token-rendering page (ASP.NET Core anti-forgery
///   calls <c>SetDoNotCacheHeaders</c> whenever the layout renders the token). Left as-is, the SW could never
///   cache a public page and offline browsing would be impossible. This relaxes that blanket to <c>no-cache</c>
///   — never <c>no-store</c> — so the SW may cache the landing / <c>/discover</c> / Details / search pages for
///   offline reading, while shared and HTTP caches still revalidate (no page is served stale without a
///   round-trip). CSRF is unaffected: the double-submit token↔cookie check runs on POST, not on caching.</item>
///   <item><b><c>/offline</c> → <c>Cache-Control: no-cache</c></b> always (the SW precaches it; it must never
///   be marked no-store even for an authenticated viewer, or offline browsing loses its fallback).</item>
/// </list>
/// </summary>
/// <remarks>
/// The header is decided from a <see cref="HttpResponse.OnStarting(System.Func{object, System.Threading.Tasks.Task}, object)"/>
/// callback so the response <c>Content-Type</c> and the authenticated principal are both known, and so it wins
/// over the anti-forgery header set during view rendering. Registered <b>after</b>
/// <c>UseAuthentication</c>/<c>UseAuthorization</c> so <c>HttpContext.User</c> is populated; static files are
/// served upstream by <c>UseStaticFiles</c>, so their long-lived immutable caching is never touched here.
/// Anonymous non-GET responses (e.g. a failed login re-render) keep the framework's <c>no-store</c> intact.
/// </remarks>
public sealed class PwaCacheControlMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Initializes the middleware with the next delegate in the pipeline.</summary>
    /// <param name="next">The next delegate to invoke.</param>
    public PwaCacheControlMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Registers the cache-policy callback and invokes the rest of the pipeline.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the downstream pipeline has finished.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var response = ctx.Response;

            // Only shape HTML documents — leave static assets, JSON/ProblemDetails and every non-HTML alone.
            if (response.ContentType is not { } contentType
                || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            // The public offline fallback is always cacheable (the SW precaches it) — never no-store.
            if (ctx.Request.Path.Equals("/offline", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.CacheControl = "no-cache";
            }
            else if (ctx.User.Identity?.IsAuthenticated == true)
            {
                // Personalized page — the SW must never cache it (privacy on a shared device).
                response.Headers.CacheControl = "no-store";
            }
            else if (ctx.Request.Path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase))
            {
                // Anti-forgery-token account pages (login / register / denied and the external-login callback
                // family) are NOT in the offline scope (design §2.3 = landing / /discover / Details / search).
                // Keep the framework's no-store so the SW never caches a token-rendering page — the anonymous
                // GET relaxation below must not reach them (path-prefix so it covers the callback children too).
                response.Headers.CacheControl = "no-store";
            }
            else if (HttpMethods.IsGet(ctx.Request.Method))
            {
                // Anonymous public GET page — cacheable for offline browsing (drop the anti-forgery blanket
                // no-store; keep no-cache so shared/HTTP caches still revalidate).
                response.Headers.CacheControl = "no-cache";
            }
            // Anonymous non-GET: leave the framework's no-store intact (the SW ignores non-GET anyway).

            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}

/// <summary>Pipeline-registration helper for <see cref="PwaCacheControlMiddleware"/>.</summary>
public static class PwaCacheControlMiddlewareExtensions
{
    /// <summary>Adds <see cref="PwaCacheControlMiddleware"/> to the request pipeline.</summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <see cref="IApplicationBuilder"/> so calls can be chained.</returns>
    public static IApplicationBuilder UsePwaCacheControl(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PwaCacheControlMiddleware>();
    }
}
