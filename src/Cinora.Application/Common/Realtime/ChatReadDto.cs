namespace Cinora.Application.Common.Realtime;

/// <summary>
/// The realtime wire model for a read-position advance pushed over <see cref="Interfaces.IChatNotifier"/> (§8).
/// It tells the other participants that <see cref="ReaderUserId"/> has read the conversation up to
/// <see cref="LastReadAtUtc"/>, so their sent bubbles can flip to "seen" without a reload. An Application-owned
/// record — never a Domain entity on the wire.
/// </summary>
/// <param name="ConversationId">The conversation whose read position advanced.</param>
/// <param name="ReaderUserId">The user who read (the viewer whose receipts the others update against).</param>
/// <param name="LastReadAtUtc">The UTC instant the reader has now read up to.</param>
public sealed record ChatReadDto(
    Guid ConversationId,
    Guid ReaderUserId,
    DateTime LastReadAtUtc);
