namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The serving-side counterpart to the local <see cref="Cinora.Application.Common.Interfaces.IFileStorage"/>
/// minting path, consumed by the Web media controller (ADR 0013 §2.4). It verifies a serving token and RESOLVES
/// the referenced file to a physical path, forced true content-type, and a cheap conditional-GET validator —
/// keeping all token verification and path resolution inside Infrastructure so the controller stays thin. This
/// seam is <b>local-provider specific</b>: a future Azure Blob adapter would instead have
/// <see cref="Cinora.Application.Common.Interfaces.IFileStorage.GetUrl"/> return a real SAS and would not use a
/// serving controller at all, so this interface deliberately does not live on the Application port.
/// </summary>
public interface IAvatarContentSource
{
    /// <summary>
    /// Verifies the serving token (HMAC + expiry + key grammar), then RESOLVES the referenced file WITHOUT
    /// reading its bytes — it returns the physical path, the forced true content-type, and a cheap weak
    /// validator (file length + last-write-time) derived from a single stat. The controller streams the path via
    /// <c>PhysicalFileResult</c>, so a matching <c>If-None-Match</c> short-circuits to <c>304</c> BEFORE the file
    /// is opened (Milestone 6.4 §6.3 — no per-GET read + rehash). Returns <see langword="null"/> — mapped to
    /// <c>404</c> by the controller — for any invalid, tampered, or expired token, a malformed/traversal key, or
    /// a missing file. Never trusts or echoes a client content-type/path.
    /// </summary>
    /// <param name="token">The opaque serving token from the request query string.</param>
    /// <returns>The resolved avatar file, or <see langword="null"/> when it cannot be served.</returns>
    AvatarFile? TryResolve(string? token);
}

/// <summary>
/// A verified avatar ready to serve: its absolute <paramref name="PhysicalPath"/> (under <c>App_Data/uploads</c>,
/// streamed through the token-gated controller — never static-served), the forced true
/// <paramref name="ContentType"/> (derived from the confirmed extension, served with <c>nosniff</c>), and a
/// cheap <b>weak</b> <paramref name="ETag"/> (<c>W/"length-lastWriteTicks"</c>) so a conditional GET can 304
/// before any file read. Weak because it is not a content hash — RFC 7232 weak comparison (which
/// <c>If-None-Match</c> uses) matches it correctly, and a byte change alters the length or the write time.
/// </summary>
/// <param name="PhysicalPath">The absolute on-disk path of the file to stream.</param>
/// <param name="ContentType">The forced <c>image/*</c> content-type to serve with <c>nosniff</c>.</param>
/// <param name="ETag">The weak validator ETag (<c>W/"length-lastWriteTicks"</c>).</param>
public sealed record AvatarFile(string PhysicalPath, string ContentType, string ETag);
