using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// The presentation model for the <c>_SearchError</c> partial (Milestone 2.4), reusing the 2.3
/// <c>_RailError</c> grammar. A post-resilience TMDB failure on the results action returns this with HTTP 200
/// (NOT a 500 — HTMX won't swap a non-2xx), rendered <c>role="alert"</c> with <c>aria-busy="false"</c>. Its
/// Retry control re-fires the SAME results GET echoing <see cref="Query"/>/<see cref="Media"/>/<see cref="Page"/>
/// with attribute-driven HTMX only (no <c>hx-on</c>, no inline JS) so the strict <c>script-src 'self'</c> CSP
/// is never touched.
/// </summary>
/// <param name="Query">The query echoed back into the Retry <c>hx-get</c>.</param>
/// <param name="Media">The media type echoed back into the Retry <c>hx-get</c>.</param>
/// <param name="Page">The page that failed, echoed back so Retry re-attempts exactly that page.</param>
public sealed record SearchErrorViewModel(string Query, MediaType Media, int Page);
