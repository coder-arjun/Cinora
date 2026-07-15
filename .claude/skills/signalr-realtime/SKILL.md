---
name: signalr-realtime
description: Use when building or debugging realtime features in Cinora — pushing notifications or feed updates to browsers, hub authentication, dropped/reconnecting connections, or scaling SignalR across multiple servers.
---

# SignalR Realtime

## Overview
Cinora pushes notifications and feed events through two strongly-typed hubs (`NotificationHub`, `FeedHub`); every user joins a personal group so any server — including Hangfire jobs via `IHubContext` — can reach all of that user's devices.

## Quick Reference
| Task | Approach |
|---|---|
| Push to one user (all devices) | `Clients.Group($"user-{userId}")` |
| Compile-safe client methods | `Hub<INotificationClient>` strongly-typed hub |
| Send from jobs/handlers | Inject `IHubContext<NotificationHub, INotificationClient>` |
| Auth | `[Authorize]` on hub; Identity cookie flows automatically same-origin |
| Reconnect (JS client) | `.withAutomaticReconnect([0, 2000, 10000, 30000])` |
| Scale-out | `AddSignalR().AddStackExchangeRedis(...)` backplane |

## Pattern
```csharp
public interface INotificationClient
{
    // WHY: strongly-typed hub — renaming a method breaks the build, not production
    Task ReceiveNotification(NotificationDto notification);
    Task UnreadCountChanged(int count);
}

[Authorize] // WHY: anonymous connections must never join user groups
public sealed class NotificationHub : Hub<INotificationClient>
{
    public override async Task OnConnectedAsync()
    {
        // WHY: group-per-user fans out to every open tab/device the user has;
        // Context.UserIdentifier comes from the Identity cookie's NameIdentifier claim
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{Context.UserIdentifier}");
        await base.OnConnectedAsync();
    }
}

// From application code (MediatR handler, Hangfire job):
public sealed class NotifyReviewLikedHandler(
    IHubContext<NotificationHub, INotificationClient> hub)
{
    public Task HandleAsync(Guid recipientId, NotificationDto dto, CancellationToken ct) =>
        hub.Clients.Group($"user-{recipientId}").ReceiveNotification(dto);
}
```

Registration: `builder.Services.AddSignalR().AddStackExchangeRedis(redisConnString, o => o.Configuration.ChannelPrefix = RedisChannel.Literal("cinora-signalr"));` — the backplane relays messages between server instances so a group send reaches connections on other nodes. Reuse the Redis instance from `.claude/skills/redis-caching/SKILL.md`.

## SignalR vs Polling
Use SignalR for latency-sensitive, user-visible events: new notifications, feed items, live like counts. Prefer plain HTTP (or short polling) for data the user pulls on navigation anyway — don't hold a socket to refresh a watchlist page. For users with the tab closed, pair with `.claude/skills/web-push-notifications/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Sending EF entities to clients | Map to DTOs — entities drag lazy-loaded graphs and leak fields |
| Group join only in `OnConnectedAsync` of one hub | Each hub manages its own groups; duplicate the join in `FeedHub` |
| Heavy work inside hub methods | Enqueue to Hangfire (see `.claude/skills/hangfire-background-jobs/SKILL.md`); hubs should stay thin |
| No backplane with 2+ instances | Group sends silently miss users on other nodes — add Redis backplane |
| Missing `[Authorize]` | `Context.UserIdentifier` is null and groups collapse to `user-` |
| Infinite default reconnect assumptions | Handle `onclose` — after retries are exhausted, prompt a page refresh |
