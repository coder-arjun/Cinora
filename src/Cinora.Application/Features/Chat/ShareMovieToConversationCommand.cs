using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Blocks;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Chat;

/// <summary>
/// Shares a movie/series into a conversation the current user is a member of (movie-sharing feature) — the same
/// participation + DM-block gate as <see cref="SendMessageCommand"/>. The message carries the denormalized movie
/// card (id/type/title/poster) plus an optional caption; after the commit it is pushed best-effort to the
/// conversation's current members. The actor is server-resolved (ADR 0009).
/// </summary>
/// <param name="ConversationId">The conversation to share into.</param>
/// <param name="TmdbId">The shared title's TMDB id.</param>
/// <param name="MediaType">The media-type token (the <c>MediaType</c> enum name, e.g. "Movie"/"Series").</param>
/// <param name="Title">The shared title's display name.</param>
/// <param name="PosterPath">The shared title's raw TMDB poster path, or <c>null</c>.</param>
/// <param name="Caption">An optional caption to accompany the card.</param>
public sealed record ShareMovieToConversationCommand(
    Guid ConversationId,
    int TmdbId,
    string MediaType,
    string Title,
    string? PosterPath,
    string? Caption) : IRequest<ChatMessageVm>;

/// <summary>Validates <see cref="ShareMovieToConversationCommand"/>: a target conversation, a real title, a bounded caption.</summary>
public sealed class ShareMovieToConversationCommandValidator : AbstractValidator<ShareMovieToConversationCommand>
{
    /// <summary>Configures the rules.</summary>
    public ShareMovieToConversationCommandValidator()
    {
        RuleFor(command => command.ConversationId).NotEmpty().WithMessage("A conversation is required.");
        RuleFor(command => command.TmdbId).GreaterThan(0).WithMessage("A movie is required.");
        RuleFor(command => command.Title).NotEmpty().WithMessage("A movie title is required.");
        RuleFor(command => command.Caption)
            .MaximumLength(Message.BodyMaxLength).WithMessage($"A caption must be at most {Message.BodyMaxLength} characters.");
    }
}

/// <summary>Handles <see cref="ShareMovieToConversationCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting sender (ADR 0009).</param>
/// <param name="chatNotifier">The best-effort realtime push port (§4).</param>
public sealed class ShareMovieToConversationCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IChatNotifier chatNotifier)
    : IRequestHandler<ShareMovieToConversationCommand, ChatMessageVm>
{
    /// <inheritdoc />
    public async Task<ChatMessageVm> Handle(ShareMovieToConversationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        if (members.Count == 0)
        {
            throw new NotFoundException($"Conversation ({request.ConversationId}) was not found.");
        }

        if (!members.Contains(me))
        {
            throw new ForbiddenAccessException("You are not a member of this conversation.");
        }

        var conversation = await db.Conversations
            .FirstAsync(c => c.Id == request.ConversationId, cancellationToken);

        if (conversation.Type == ConversationType.Direct && members.Count == 2)
        {
            var other = members.First(u => u != me);
            if (await BlockQueries.AreBlockedEitherWayAsync(db, me, other, cancellationToken))
            {
                throw new ForbiddenAccessException("You cannot message this user.");
            }
        }

        // Domain guards the movie coordinates + caption length → DomainException → 400.
        var message = Message.CreateSharedMovie(
            request.ConversationId, me, request.TmdbId, request.MediaType, request.Title, request.PosterPath, request.Caption);
        db.Messages.Add(message);
        conversation.BumpLastMessage(message.SentAtUtc);

        await db.SaveChangesAsync(cancellationToken);

        var sender = await db.Users.AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.DisplayName, u.AvatarFileKey })
            .FirstAsync(cancellationToken);

        await chatNotifier.MessageSentAsync(
            request.ConversationId,
            members,
            new ChatMessageDto(
                message.Id, request.ConversationId, me, sender.DisplayName, message.Body, message.SentAtUtc,
                message.SharedMovieTmdbId, message.SharedMovieMediaType, message.SharedMovieTitle, message.SharedMoviePosterPath),
            cancellationToken);

        return new ChatMessageVm(
            message.Id, me, sender.DisplayName, sender.AvatarFileKey, message.Body, message.SentAtUtc,
            IsMine: true, IsDeleted: false,
            message.SharedMovieTmdbId, message.SharedMovieMediaType, message.SharedMovieTitle, message.SharedMoviePosterPath);
    }
}
