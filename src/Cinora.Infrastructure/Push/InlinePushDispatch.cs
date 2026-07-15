using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinora.Infrastructure.Push;

/// <summary>
/// The free, no-Hangfire <see cref="IPushDispatch"/> adapter that ships now (ADR 0020 §3.4): it runs the Web Push
/// fan-out on a background <see cref="Task"/> in its <b>own</b> DI scope, so the send is <b>off the request
/// thread</b> and never touches the request's about-to-be-disposed scope. <see cref="Enqueue"/> returns
/// immediately and the send is <b>fire-and-forget best-effort</b> — a bounded timeout caps a slow push service,
/// and every fault (including the timeout) is caught and logged, so a push failure can never surface as an
/// unobserved task exception nor affect the already-committed write (the inbox is authoritative; N2/ADR 0011).
/// A durable, retried <c>HangfirePushDispatch</c> is the documented drop-in when a queue is wanted; swapping the
/// DI registration is the only change (the port stays).
/// </summary>
/// <param name="scopeFactory">Creates a fresh DI scope per dispatch so each send gets a clean, isolated context.</param>
/// <param name="logger">Records a Warning when a best-effort dispatch fails.</param>
internal sealed partial class InlinePushDispatch(
    IServiceScopeFactory scopeFactory,
    ILogger<InlinePushDispatch> logger)
    : IPushDispatch
{
    // Caps a slow/hung push service so a background dispatch can never run unbounded.
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public void Enqueue(Guid notificationId)
    {
        // Fire-and-forget: the task owns its own scope + timeout and swallows every fault, so nothing here can
        // throw back to the producing handler or leave an unobserved exception. Assigned to a discard so the
        // un-awaited task is explicit (not an accidental omission).
        _ = Task.Run(() => DispatchAsync(notificationId));
    }

    /// <inheritdoc />
    public void EnqueueChatMessage(Guid messageId)
    {
        // Fire-and-forget, same contract as Enqueue: owns its scope + timeout and swallows every fault.
        _ = Task.Run(() => DispatchChatAsync(messageId));
    }

    private async Task DispatchAsync(Guid notificationId)
    {
        using var timeout = new CancellationTokenSource(DispatchTimeout);
        try
        {
            // A FRESH scope (from the singleton scope factory) — never the request's disposed scope. Resolve a
            // clean ISender and dispatch the send command (ADR 0008: a job/dispatcher orchestrates via ISender).
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new SendPushNotificationCommand(notificationId), timeout.Token);
        }
        catch (Exception exception)
        {
            // Terminal best-effort boundary: log (including a timeout OperationCanceledException) and never rethrow
            // — the notification is already committed and visible in the inbox regardless of push delivery.
            LogDispatchFailed(logger, exception, notificationId);
        }
    }

    private async Task DispatchChatAsync(Guid messageId)
    {
        using var timeout = new CancellationTokenSource(DispatchTimeout);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new SendChatPushCommand(messageId), timeout.Token);
        }
        catch (Exception exception)
        {
            // Best-effort: the message is already committed; a missed out-of-app push never affects the chat.
            LogChatDispatchFailed(logger, exception, messageId);
        }
    }

    [LoggerMessage(
        EventId = 6220,
        Level = LogLevel.Warning,
        Message = "Best-effort Web Push dispatch for notification {NotificationId} failed; the notification is " +
                  "already committed and remains in the recipient's inbox.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception, Guid notificationId);

    [LoggerMessage(
        EventId = 6222,
        Level = LogLevel.Warning,
        Message = "Best-effort chat Web Push dispatch for message {MessageId} failed; the message is already " +
                  "committed and the in-app conversation is unaffected.")]
    private static partial void LogChatDispatchFailed(ILogger logger, Exception exception, Guid messageId);
}
