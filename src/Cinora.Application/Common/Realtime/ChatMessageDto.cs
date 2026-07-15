namespace Cinora.Application.Common.Realtime;

/// <summary>
/// The realtime wire model for a single chat message pushed over <see cref="Interfaces.IChatNotifier"/> (§4). It
/// is an Application-owned record — <b>never</b> a Domain entity or EF type on the wire — carrying only the
/// primitives a client needs to append a bubble. <see cref="Body"/> is rendered as PLAIN TEXT: the client sets
/// it via <c>textContent</c> (never <c>innerHTML</c>) and any server render Razor-encodes it (§13). The
/// persisted <c>Message</c> row is the source of truth; this push is a live accelerator over it.
/// </summary>
/// <param name="Id">The message's unique identifier (also the client-side dedupe key).</param>
/// <param name="ConversationId">The conversation the message belongs to.</param>
/// <param name="SenderId">The sending user's id (the client marks it "mine" when it equals the viewer).</param>
/// <param name="SenderDisplayName">The sender's public display name (PLAIN TEXT; encoded on render).</param>
/// <param name="Body">The message text (PLAIN TEXT; set via <c>textContent</c> / Razor-encoded, never HTML). For a movie share this is the caption (may be empty).</param>
/// <param name="SentAtUtc">The UTC instant the message was sent (drives ordering and the timestamp).</param>
/// <param name="SharedMovieTmdbId">The TMDB id of a shared movie/series, or <c>null</c> for a plain message.</param>
/// <param name="SharedMovieMediaType">The shared title's media-type token (e.g. "Movie"/"Series"), or <c>null</c>.</param>
/// <param name="SharedMovieTitle">The shared title's display name (PLAIN TEXT), or <c>null</c>.</param>
/// <param name="SharedMoviePosterPath">The shared title's raw TMDB poster path, or <c>null</c>.</param>
public sealed record ChatMessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    string Body,
    DateTime SentAtUtc,
    int? SharedMovieTmdbId = null,
    string? SharedMovieMediaType = null,
    string? SharedMovieTitle = null,
    string? SharedMoviePosterPath = null);
