using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// Clears the CURRENT user's avatar, reverting to the monogram fallback (ADR 0013 §2.5). The owner is resolved
/// server-side from <see cref="ICurrentUser"/> — no id is bound. Idempotent: a user with no avatar is a no-op
/// (no write, no delete). Otherwise the key is cleared on the domain, committed, then the stored file is
/// best-effort deleted (a delete failure logs a Warning and never fails the request).
/// </summary>
public sealed record RemoveAvatarCommand : IRequest<Unit>;

/// <summary>
/// Handles <see cref="RemoveAvatarCommand"/>: load the current user, capture the old key, and — only when one is
/// set — clear it (<see cref="User.SetAvatar"/> with <c>null</c>), save, and best-effort delete the file.
/// </summary>
/// <param name="fileStorage">The storage port used for the best-effort delete of the removed file.</param>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
/// <param name="logger">Logs a Warning when the best-effort delete fails (never throws to the caller).</param>
public sealed partial class RemoveAvatarCommandHandler(
    IFileStorage fileStorage,
    IAppDbContext db,
    ICurrentUser currentUser,
    ILogger<RemoveAvatarCommandHandler> logger)
    : IRequestHandler<RemoveAvatarCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(RemoveAvatarCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        var user = await db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"User ({userId}) was not found.");

        var oldKey = user.AvatarFileKey;
        if (oldKey is null)
        {
            // Idempotent no-op: no avatar to remove, so no write and no delete.
            return Unit.Value;
        }

        user.SetAvatar(null);
        await db.SaveChangesAsync(cancellationToken);

        // Best-effort delete of the removed file — a failure leaves an orphan but never fails the request (§13).
        // CancellationToken.None on purpose: this cleanup runs AFTER the commit, so a request abort (a cancelled
        // token) must not skip it and strand the just-removed file (a future ct-honoring provider would orphan it).
        try
        {
            await fileStorage.DeleteAsync(oldKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogAvatarCleanupFailed(logger, exception, oldKey);
        }

        return Unit.Value;
    }

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Warning,
        Message = "Best-effort avatar cleanup failed for key {StorageKey}; leaving an orphan file.")]
    private static partial void LogAvatarCleanupFailed(ILogger logger, Exception exception, string storageKey);
}
