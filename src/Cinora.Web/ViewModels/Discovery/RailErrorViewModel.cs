using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// The tiny presentation model for the <c>_RailError</c> partial (Milestone 2.3 — the /review-ui HIGH fix).
/// It carries only which rail failed so the partial can render an on-brand "couldn't load this row" alert
/// whose Retry control re-fires exactly that rail's lazy load via attribute-driven, CSP-safe HTMX
/// (<c>hx-get /discover/rail?kind=…&amp;media=…</c>). No TMDB data and no Domain entities — the rail failed,
/// so there is nothing to show but the retry affordance.
/// </summary>
/// <param name="Kind">The curated rail that failed to load, echoed back into the Retry <c>hx-get</c>.</param>
/// <param name="Media">The media type the failed rail was for, echoed back into the Retry <c>hx-get</c>.</param>
public sealed record RailErrorViewModel(RailKind Kind, MediaType Media);
