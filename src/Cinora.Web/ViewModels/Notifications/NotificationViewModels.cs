namespace Cinora.Web.ViewModels.Notifications;

/// <summary>
/// The model for the <c>_NotificationsError</c> partial: the on-brand "couldn't load your notifications" state
/// served with HTTP 200 (so HTMX swaps it in place of the shimmering load-more skeleton — it never swaps a
/// non-2xx) when a lazy inbox page read fails after the query pipeline. Its Retry control re-fires the exact same
/// lazy <c>hx-get</c>.
/// </summary>
/// <param name="RetryUrl">The URL the Retry control re-GETs — the same lazy inbox page that just failed.</param>
public sealed record NotificationsErrorVm(string RetryUrl);
