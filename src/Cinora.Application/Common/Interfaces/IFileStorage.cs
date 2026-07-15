namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The storage port for user-uploaded binary media (Phase 4 avatars), behind which the free local-filesystem
/// provider ships today and a paid Blob provider could be swapped later with zero caller changes (ADR 0013).
/// The port speaks <b>streams, content-types, and opaque server-owned keys ONLY</b> — never an
/// <c>IFormFile</c>, a <c>FileInfo</c>, or a provider SDK type. The Web controller unwraps <c>IFormFile</c> to
/// a <see cref="Stream"/> + content-type before dispatch; callers never construct or interpret the key.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Persists an upload and returns the <b>server-generated</b> opaque storage key. The implementation is the
    /// authoritative upload gate: it enforces the size cap while copying (aborting past it — never buffering an
    /// unbounded stream), inspects the actual bytes with a magic-byte image check, and derives the true type;
    /// a file whose real bytes are not one of the allowed image types (JPEG, PNG, WebP) is rejected with a
    /// <see cref="Exceptions.ValidationException"/> (→ HTTP 400) <b>regardless of the declared content-type</b>.
    /// The returned key embeds the confirmed extension and a random component; the original filename is
    /// discarded, so no user-controlled path component ever reaches the filesystem.
    /// </summary>
    /// <param name="content">The upload stream (unwrapped from <c>IFormFile</c> in the Web layer).</param>
    /// <param name="declaredContentType">The client-declared content-type — NOT trusted for the type decision.</param>
    /// <param name="ct">A token to cancel the write.</param>
    /// <returns>The server-generated storage key to persist (e.g. on <c>User.AvatarFileKey</c>).</returns>
    /// <exception cref="Exceptions.ValidationException">The bytes are not an allowed image, or exceed the cap.</exception>
    Task<string> SaveAsync(Stream content, string declaredContentType, CancellationToken ct);

    /// <summary>
    /// Computes a same-origin, time-limited signed URL that serves the stored object — the free local stand-in
    /// for a Blob SAS. <b>Pure and I/O-free</b> so Razor views and tag-helpers may call it per-avatar without an
    /// async hop. The URL is byte-identical within a coarse expiry bucket (browser-cacheable) yet rotates and
    /// expires each bucket. Returns a relative path under the configured public base path.
    /// </summary>
    /// <param name="storageKey">A key previously returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ttl">The minimum time the URL must remain valid before its bucketed expiry.</param>
    /// <returns>A relative, signed, time-limited URL (e.g. <c>/uploads/avatar?t=…</c>).</returns>
    string GetUrl(string storageKey, TimeSpan ttl);

    /// <summary>
    /// Best-effort, idempotent deletion of a stored object by key (used on avatar replace/remove). A missing
    /// object is a no-op; a malformed or path-traversal-bearing key is rejected as a no-op, never a filesystem
    /// escape. Never throws for the caller — a delete failure is logged and swallowed (orphan-sweep deferred).
    /// </summary>
    /// <param name="storageKey">A key previously returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ct">A token to cancel the deletion.</param>
    /// <returns>A task that completes when the best-effort deletion has run.</returns>
    Task DeleteAsync(string storageKey, CancellationToken ct);
}
