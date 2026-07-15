using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// The tiny presentation model for the <c>_SearchPrompt</c> partial (Milestone 2.4): the calm idle/empty
/// state shown before a query is entered or when the term is shorter than the UX threshold. Returned with
/// HTTP 200 (never a 400) so HTMX swaps it in place without spinning — an empty search box is the idle state,
/// not an error. It carries only the media type so any copy/links can stay media-aware.
/// </summary>
/// <param name="Media">The media type the search is scoped to (Movie in Phase 2).</param>
public sealed record SearchPromptViewModel(MediaType Media);
