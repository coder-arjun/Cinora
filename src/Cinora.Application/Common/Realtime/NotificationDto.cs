using Cinora.Domain.Enums;

namespace Cinora.Application.Common.Realtime;

/// <summary>
/// The realtime wire model for a single notification pushed over <see cref="Interfaces.IRealtimeNotifier"/>
/// (ADR 0011). It is an Application-owned record — <b>never</b> a Domain entity or EF type on the wire — carrying
/// only the primitives a client needs to render a toast and deep-link: the id, the <see cref="NotificationType"/>
/// and optional <see cref="TargetId"/> (which together resolve the deep-link target), the server-composed
/// <see cref="Message"/>, the optional actor display name, and the creation instant. All text is treated as
/// PLAIN TEXT by the client and output-encoded on render (phase-3 XSS stance §3).
/// </summary>
/// <param name="Id">The notification's unique identifier.</param>
/// <param name="Type">The kind of event (drives the client's icon + deep-link resolution).</param>
/// <param name="Message">The short, server-composed notification text (already contains the actor's name).</param>
/// <param name="ActorDisplayName">The triggering user's display name, or <c>null</c> for a system event.</param>
/// <param name="TargetId">The related entity's id to deep-link to (a review id or a friend-request id), or <c>null</c>.</param>
/// <param name="CreatedAtUtc">The UTC instant the notification was created.</param>
public sealed record NotificationDto(
    Guid Id,
    NotificationType Type,
    string Message,
    string? ActorDisplayName,
    Guid? TargetId,
    DateTime CreatedAtUtc);
