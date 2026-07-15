using Cinora.Application.Common.Interfaces;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A hand-rolled <see cref="ICurrentUser"/> that resolves to a fixed, always-authenticated id — the server-side
/// actor a Milestone 4.2 handler edits (no id is ever bound from the request). The suite uses hand-rolled doubles
/// (no mocking package — consistent with the rest of the test projects and Central Package Management).
/// </summary>
internal sealed class StubCurrentUser(Guid userId) : ICurrentUser
{
    public Guid? UserId => userId;

    public bool IsAuthenticated => true;

    public Guid GetRequiredUserId() => userId;
}

/// <summary>
/// A hand-rolled <see cref="IFileStorage"/> that records the ordering the ADR 0013 §2.5 avatar handlers rely on:
/// what <see cref="SaveAsync"/> returns (or throws), and which keys <see cref="DeleteAsync"/> was asked to remove
/// (in order). It performs no real I/O, so the handlers are unit-testable without a filesystem.
/// </summary>
internal sealed class RecordingFileStorage : IFileStorage
{
    /// <summary>The key <see cref="SaveAsync"/> returns when it does not throw.</summary>
    public string SaveReturns { get; set; } = $"avatars/{new string('a', 32)}.jpg";

    /// <summary>When set, <see cref="SaveAsync"/> faults with this exception (the step-1-fails path).</summary>
    public Exception? SaveThrows { get; set; }

    /// <summary>When set, <see cref="DeleteAsync"/> faults with this exception (the best-effort-delete-fails path).</summary>
    public Exception? DeleteThrows { get; set; }

    /// <summary>The keys passed to <see cref="DeleteAsync"/>, in call order.</summary>
    public List<string> DeletedKeys { get; } = [];

    public Task<string> SaveAsync(Stream content, string declaredContentType, CancellationToken ct) =>
        SaveThrows is not null
            ? Task.FromException<string>(SaveThrows)
            : Task.FromResult(SaveReturns);

    public string GetUrl(string storageKey, TimeSpan ttl) => $"/uploads/avatar?t={storageKey}";

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);
        return DeleteThrows is not null ? Task.FromException(DeleteThrows) : Task.CompletedTask;
    }
}

/// <summary>
/// A hand-rolled <see cref="IUploadPolicy"/> for the avatar validator tests: a configurable size cap and a fixed
/// JPEG/PNG/WebP allow-list (case-insensitive, tolerant of a trailing media-type parameter — mirroring the real
/// Infrastructure policy the validator consumes).
/// </summary>
internal sealed class StubUploadPolicy(long maxUploadBytes) : IUploadPolicy
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public long MaxUploadBytes => maxUploadBytes;

    public bool IsAllowedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();
        return Allowed.Contains(mediaType);
    }
}
