using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Entities;

/// <summary>
/// A single chat message. The <see cref="Body"/> is user text — PLAIN TEXT, Razor output-encoded on render,
/// never HTML (§13). Deleting is a soft tombstone (<see cref="IsDeleted"/>) so keyset history + read positions
/// stay stable; the UI renders a "message deleted" placeholder in its place. A message may instead (or also)
/// carry a shared MOVIE reference (<see cref="SharedMovieTmdbId"/> et al.), in which case the UI renders a movie
/// card; the <see cref="Body"/> then holds an optional caption (possibly empty).
/// </summary>
public sealed class Message
{
    /// <summary>The maximum length of a message <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 4000;

    /// <summary>The maximum length of a shared movie's title.</summary>
    public const int SharedMovieTitleMaxLength = 300;

    /// <summary>The maximum length of a shared movie's media-type token and poster path.</summary>
    public const int SharedMovieFieldMaxLength = 300;

    private Message()
    {
    }

    /// <summary>The unique identifier of this message.</summary>
    public Guid Id { get; private set; }

    /// <summary>The conversation this message belongs to.</summary>
    public Guid ConversationId { get; private set; }

    /// <summary>The sender's user id.</summary>
    public Guid SenderId { get; private set; }

    /// <summary>The message text (plain text; Razor-encoded on render). For a movie share this is the caption (may be empty).</summary>
    public string Body { get; private set; } = null!;

    /// <summary>The UTC instant the message was sent (part of the history keyset).</summary>
    public DateTime SentAtUtc { get; private set; }

    /// <summary>Whether the sender soft-deleted this message (rendered as a "message deleted" tombstone).</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>The TMDB id of a shared movie/series, or <c>null</c> for a plain-text message.</summary>
    public int? SharedMovieTmdbId { get; private set; }

    /// <summary>The shared title's media type (the <c>MediaType</c> enum name, e.g. "Movie"/"Series"), or <c>null</c>.</summary>
    public string? SharedMovieMediaType { get; private set; }

    /// <summary>The shared title's display name (denormalized so the card renders standalone), or <c>null</c>.</summary>
    public string? SharedMovieTitle { get; private set; }

    /// <summary>The shared title's raw TMDB poster path (rendered via the TMDB image seam), or <c>null</c>.</summary>
    public string? SharedMoviePosterPath { get; private set; }

    /// <summary>Creates a plain-text message.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="senderId">The sender.</param>
    /// <param name="body">The text; required, max <see cref="BodyMaxLength"/> characters.</param>
    /// <returns>A new <see cref="Message"/>.</returns>
    /// <exception cref="DomainException">Thrown when the body is blank or too long.</exception>
    public static Message Create(Guid conversationId, Guid senderId, string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > BodyMaxLength)
        {
            throw new DomainException($"A message body is required and must be at most {BodyMaxLength} characters.");
        }

        return new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = senderId,
            Body = body,
            SentAtUtc = DateTime.UtcNow,
            IsDeleted = false,
        };
    }

    /// <summary>Creates a message that shares a movie/series card, with an optional caption.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="senderId">The sender.</param>
    /// <param name="tmdbId">The shared title's TMDB id (must be positive).</param>
    /// <param name="mediaType">The media-type token (the <c>MediaType</c> enum name), required.</param>
    /// <param name="title">The shared title's display name, required.</param>
    /// <param name="posterPath">The raw TMDB poster path, or <c>null</c>.</param>
    /// <param name="caption">An optional caption (may be null/empty), max <see cref="BodyMaxLength"/> characters.</param>
    /// <returns>A new <see cref="Message"/> carrying the shared-movie reference.</returns>
    /// <exception cref="DomainException">Thrown when the movie coordinates are invalid or the caption is too long.</exception>
    public static Message CreateSharedMovie(
        Guid conversationId, Guid senderId, int tmdbId, string mediaType, string title, string? posterPath, string? caption)
    {
        if (tmdbId <= 0)
        {
            throw new DomainException("A shared movie needs a valid TMDB id.");
        }

        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > SharedMovieFieldMaxLength)
        {
            throw new DomainException("A shared movie needs a media type.");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > SharedMovieTitleMaxLength)
        {
            throw new DomainException($"A shared movie title is required and must be at most {SharedMovieTitleMaxLength} characters.");
        }

        var body = caption?.Trim() ?? string.Empty;
        if (body.Length > BodyMaxLength)
        {
            throw new DomainException($"A caption must be at most {BodyMaxLength} characters.");
        }

        if (posterPath is not null && posterPath.Length > SharedMovieFieldMaxLength)
        {
            posterPath = posterPath[..SharedMovieFieldMaxLength];
        }

        return new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = senderId,
            Body = body,
            SentAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            SharedMovieTmdbId = tmdbId,
            SharedMovieMediaType = mediaType.Trim(),
            SharedMovieTitle = title.Trim(),
            SharedMoviePosterPath = posterPath,
        };
    }

    /// <summary>Soft-deletes the message (keeps the row; renders as a tombstone).</summary>
    public void MarkDeleted() => IsDeleted = true;
}
