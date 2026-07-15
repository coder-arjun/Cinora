namespace Cinora.Web.Infrastructure;

/// <summary>
/// Writes Cinora's baseline OWASP security response headers on every response: a strict
/// Content-Security-Policy, <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c> and a restrictive
/// <c>Permissions-Policy</c> (Milestone 1.6b / F2). HSTS and HTTP&#8594;HTTPS redirection are handled
/// separately by the framework's <c>UseHsts</c>/<c>UseHttpsRedirection</c> in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// Headers are set before the downstream pipeline runs, so every successful response (HTML, static
/// <c>/dist/</c> assets, HTMX partials, 302s, 404s and 429s) carries them. Responses that the global
/// exception handler regenerates after resetting the response (ProblemDetails for 500 / mapped 4xx) are
/// the documented exception — those are JSON API errors where CSP is not security-relevant.
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    // Everything is same-origin ('self'). Milestone 1.6a verified NO inline <script>/<style> or inline
    // event handlers exist, so a strict script-src/style-src 'self' needs neither a nonce nor
    // 'unsafe-inline'/'unsafe-eval'. frame-ancestors 'none' supersedes X-Frame-Options (clickjacking).
    // Phase 2 (Milestone 2.3) APPLIED: img-src is widened to allow https://image.tmdb.org so TMDB
    // posters/backdrops/cast profiles load (data: kept for the skeleton placeholder); NO other directive is
    // widened — the HTMX rail GETs are same-origin (connect-src 'self') and 2.3 uses no inline/eval script.
    // Phase 2 (Milestone 2.4) APPLIED: interactive Search ships the eval-free @alpinejs/csp build, so
    // script-src stays 'self' with NO 'unsafe-eval' — this directive block is UNCHANGED by 2.4 (the only 2.4
    // security-relevant change was the npm build swap). Do not widen this further.
    // Post-Phase-6 APPLIED: img-src also allows https://images.weserv.nl — the free image proxy/CDN posters
    // are routed through (TMDB's image.tmdb.org CDN is unreachable from the deployed host and blocked on some
    // client networks, so posters load via weserv instead). image.tmdb.org is kept (the SW/legacy references
    // it). This is the ONLY img-src host added; no other directive widened.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' https://image.tmdb.org https://images.weserv.nl data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    // Deny the powerful features Cinora never uses, for the page and every embedded browsing context.
    // Unknown directives are ignored by browsers, so an over-broad list is harmless; this stays to the
    // widely-recognized sensor/capture/payment features.
    private const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), " +
        "fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    private readonly RequestDelegate _next;

    /// <summary>Initializes the middleware with the next delegate in the pipeline.</summary>
    /// <param name="next">The next delegate to invoke after the headers are set.</param>
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Adds the security headers to the response, then invokes the rest of the pipeline.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the downstream pipeline has finished.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["Permissions-Policy"] = PermissionsPolicy;

        return _next(context);
    }
}

/// <summary>Pipeline-registration helpers for <see cref="SecurityHeadersMiddleware"/>.</summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>Adds <see cref="SecurityHeadersMiddleware"/> to the request pipeline.</summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <see cref="IApplicationBuilder"/> so calls can be chained.</returns>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
