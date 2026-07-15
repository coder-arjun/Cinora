using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// Replaces the CURRENT user's avatar with a freshly uploaded image (ADR 0013 §2.5). The command carries a raw
/// <see cref="Stream"/> (unwrapped from <c>IFormFile</c> in the Web controller — the Web type never crosses into
/// Application) plus the client-declared content-type and byte length; the owner is resolved server-side from
/// <see cref="ICurrentUser"/>, so no user id is bound. The pipeline behaviors log only the request TYPE NAME —
/// never the stream — so logging is harmless (verified: <c>LoggingBehavior</c>/<c>PerformanceBehavior</c> emit
/// <c>typeof(TRequest).Name</c>; the validator reads only <c>Length</c>/<c>ContentType</c>).
/// </summary>
/// <param name="Content">The upload stream to persist (read once by <see cref="IFileStorage.SaveAsync"/>).</param>
/// <param name="ContentType">The client-declared content-type — the friendly pre-check only; NOT trusted for
/// the true-type decision, which the storage adapter makes by magic-byte inspection.</param>
/// <param name="Length">The declared byte length — the friendly size pre-check before the stream is read.</param>
public sealed record ChangeAvatarCommand(Stream Content, string ContentType, long Length)
    : IRequest<ChangeAvatarResult>;

/// <summary>The outcome of a <see cref="ChangeAvatarCommand"/>.</summary>
/// <param name="AvatarFileKey">The server-generated storage key now persisted on <c>User.AvatarFileKey</c>.</param>
public sealed record ChangeAvatarResult(string AvatarFileKey);

/// <summary>
/// Validates <see cref="ChangeAvatarCommand"/> for a fast, friendly 400 BEFORE the stream is read — the first
/// arm of the two-tier upload gate (ADR 0013 §2.3). It reads the allow-list and size cap from the
/// <see cref="IUploadPolicy"/> port (implemented in Infrastructure over <c>FileStorageOptions</c>) so the
/// Application stays free of Infrastructure's Options type (the dependency rule). Defense-in-depth only:
/// <see cref="IFileStorage.SaveAsync"/> remains the authoritative magic-byte + size gate regardless of what the
/// client declared.
/// </summary>
public sealed class ChangeAvatarCommandValidator : AbstractValidator<ChangeAvatarCommand>
{
    /// <summary>Configures the rules for <see cref="ChangeAvatarCommand"/> from the upload policy.</summary>
    /// <param name="uploadPolicy">Surfaces the size cap and content-type allow-list (the dependency-rule seam).</param>
    public ChangeAvatarCommandValidator(IUploadPolicy uploadPolicy)
    {
        ArgumentNullException.ThrowIfNull(uploadPolicy);

        RuleFor(command => command.Length)
            .GreaterThan(0).WithMessage("Please choose an image to upload.")
            .LessThanOrEqualTo(uploadPolicy.MaxUploadBytes)
            .WithMessage($"The image cannot exceed {uploadPolicy.MaxUploadBytes} bytes.");

        RuleFor(command => command.ContentType)
            .Must(uploadPolicy.IsAllowedContentType)
            .WithMessage("The image must be a JPEG, PNG, or WebP file.");
    }
}

/// <summary>
/// Handles <see cref="ChangeAvatarCommand"/> as the exact ADR 0013 §2.5 orchestration, keeping the store and the
/// DB consistent with no orphan on the happy path:
/// <list type="number">
///   <item>persist + validate the new bytes (<see cref="IFileStorage.SaveAsync"/> — the authoritative gate);</item>
///   <item>load the current user, capture the old key, <see cref="User.SetAvatar"/> the new key, and save;</item>
///   <item>if the save committed and there was an old key, best-effort delete the replaced file.</item>
/// </list>
/// If step 2 throws AFTER step 1 saved bytes, the just-saved key is best-effort deleted in the catch to avoid an
/// orphan, then the exception is rethrown (the DB is left unchanged). A best-effort delete failure logs a
/// Warning and never fails the request (orphan-sweep deferred, §13).
/// </summary>
/// <param name="fileStorage">The storage port (upload/mint/delete) — the local free provider today.</param>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
/// <param name="logger">Logs a Warning when a best-effort avatar delete fails (never throws to the caller).</param>
public sealed partial class ChangeAvatarCommandHandler(
    IFileStorage fileStorage,
    IAppDbContext db,
    ICurrentUser currentUser,
    ILogger<ChangeAvatarCommandHandler> logger)
    : IRequestHandler<ChangeAvatarCommand, ChangeAvatarResult>
{
    /// <inheritdoc />
    public async Task<ChangeAvatarResult> Handle(ChangeAvatarCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        // Step 1: persist + validate the bytes. Throws ValidationException (→ 400) on a non-image or oversize
        // upload REGARDLESS of the declared content-type; on failure here the DB is untouched and no file remains.
        var newKey = await fileStorage.SaveAsync(request.Content, request.ContentType, cancellationToken);

        string? oldKey;
        try
        {
            // Step 2: point the user at the new key and commit. Load a tracked aggregate → domain method → save.
            var user = await db.Users
                .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
                ?? throw new NotFoundException($"User ({userId}) was not found.");

            oldKey = user.AvatarFileKey;
            user.SetAvatar(newKey);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Step 2 failed after step 1 wrote the bytes: best-effort delete the just-saved file so it is not
            // orphaned, then rethrow the original fault (the DB is unchanged — the save rolled back / never ran).
            // CancellationToken.None on purpose: if the fault that triggered this compensation WAS a cancellation,
            // the request token is already cancelled — a future ct-honoring provider would skip the cleanup and
            // orphan the just-saved file, defeating the whole point of the catch.
            await BestEffortDeleteAsync(newKey, CancellationToken.None);
            throw;
        }

        // Step 3: the new key is committed. Best-effort delete the replaced file — a failure here leaves an
        // orphan but must NEVER fail the request (an orphan-sweep chore reconciles later, §13).
        if (oldKey is not null)
        {
            // CancellationToken.None on purpose: this cleanup runs AFTER the commit, so a request abort must not
            // skip it and strand the replaced file (a future ct-honoring provider would otherwise orphan it).
            await BestEffortDeleteAsync(oldKey, CancellationToken.None);
        }

        return new ChangeAvatarResult(newKey);
    }

    // Swallows any delete fault so it can neither fail the request nor mask an in-flight rollback exception.
    private async Task BestEffortDeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            await fileStorage.DeleteAsync(storageKey, cancellationToken);
        }
        catch (Exception exception)
        {
            LogAvatarCleanupFailed(logger, exception, storageKey);
        }
    }

    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Warning,
        Message = "Best-effort avatar cleanup failed for key {StorageKey}; leaving an orphan file.")]
    private static partial void LogAvatarCleanupFailed(ILogger logger, Exception exception, string storageKey);
}
