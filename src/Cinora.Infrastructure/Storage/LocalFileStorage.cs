using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Options;
using FluentValidation.Results;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The free local-filesystem realization of <see cref="IFileStorage"/> and the authoritative avatar upload
/// gate (ADR 0013). Bytes are written under <see cref="FileStorageOptions.LocalRootPath"/> resolved relative to
/// the content root, deliberately <b>outside <c>wwwroot</c></b>, using a server-generated key derived from the
/// magic-byte-confirmed type — the client filename and declared content-type are discarded, so path traversal
/// is impossible by construction. It also serves those bytes back (via <see cref="IAvatarContentSource"/>)
/// through the token-gated media controller. Registered as a singleton — it holds no per-request state.
/// </summary>
internal sealed partial class LocalFileStorage : IFileStorage, IAvatarContentSource
{
    // The peek size for magic-byte detection and the streaming copy buffer.
    private const int CopyBufferSize = 81920;

    private readonly IOptions<FileStorageOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly IImageContentInspector _inspector;
    private readonly AvatarUrlSigner _signer;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(
        IOptions<FileStorageOptions> options,
        IHostEnvironment environment,
        IImageContentInspector inspector,
        AvatarUrlSigner signer,
        ILogger<LocalFileStorage> logger)
    {
        _options = options;
        _environment = environment;
        _inspector = inspector;
        _signer = signer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(Stream content, string declaredContentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var options = _options.Value;

        // 1) Peek the header and derive the TRUE type from magic bytes. The declared content-type is NOT
        // trusted for this decision — a file whose real bytes are not JPEG/PNG/WebP (SVG, HTML, anything) is
        // rejected here regardless of what the client declared.
        var header = new byte[ImageContentInspector.RequiredHeaderBytes];
        var headerLength = await content
            .ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);

        if (!_inspector.TryDetect(header.AsSpan(0, headerLength), out var format))
        {
            throw NotAnAllowedImage();
        }

        var maxBytes = options.MaxUploadBytes;
        if (headerLength > maxBytes)
        {
            throw TooLarge(maxBytes);
        }

        // 2) Mint the server-owned key from the CONFIRMED type and resolve its path inside the upload root.
        var storageKey = AvatarStorageKey.New(format);
        var root = AvatarPaths.ResolveRoot(_environment, options);
        if (!AvatarPaths.TryResolveFile(root, storageKey, out var fullPath))
        {
            // Unreachable in practice (a freshly minted key is always valid), but never write outside the root.
            throw NotAnAllowedImage();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // 3) Stream to disk, enforcing the size cap WHILE copying — abort past the cap, never buffer the whole
        // (possibly unbounded) stream. CreateNew guarantees we never overwrite an existing file.
        try
        {
            await using var destination = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true);

            long total = headerLength;
            await destination.WriteAsync(header.AsMemory(0, headerLength), ct).ConfigureAwait(false);

            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw TooLarge(maxBytes);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort cleanup of the partial file on any failure (validation, cancellation, or I/O).
            TryDeleteFile(fullPath);
            throw;
        }

        return storageKey;
    }

    /// <inheritdoc />
    public string GetUrl(string storageKey, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);

        // Defense-in-depth: refuse to mint a URL for a key that is not a well-formed avatar key.
        if (!AvatarStorageKey.IsValid(storageKey))
        {
            throw new ArgumentException("The storage key is not a valid avatar key.", nameof(storageKey));
        }

        // MINT side of the mint↔serve coupling: the URL base comes from the configurable PublicBasePath, whose
        // default and boot-time invariant are the shared AvatarServingRoute const that the MediaController route
        // also uses. FileStorageRouteValidateOptions fails boot if PublicBasePath drifts from that route, so a
        // minted URL always points at a path the controller actually serves (ADR 0013 §2.4).
        var token = _signer.CreateToken(storageKey, ttl);
        var basePath = _options.Value.PublicBasePath.TrimEnd('/');
        return $"{basePath}/avatar?t={token}";
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        // Best-effort + idempotent + defense-in-depth. A malformed / '..'-bearing key never resolves to a path,
        // so it is a silent no-op rather than a filesystem escape.
        if (AvatarStorageKey.IsValid(storageKey))
        {
            var root = AvatarPaths.ResolveRoot(_environment, _options.Value);
            if (AvatarPaths.TryResolveFile(root, storageKey, out var fullPath))
            {
                TryDeleteFile(fullPath);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public AvatarFile? TryResolve(string? token)
    {
        // Verify the token (HMAC + expiry) and extract the key; anything invalid/expired/tampered → null (404).
        if (!_signer.TryValidateToken(token, out var storageKey) || !AvatarStorageKey.IsValid(storageKey))
        {
            return null;
        }

        var root = AvatarPaths.ResolveRoot(_environment, _options.Value);
        if (!AvatarPaths.TryResolveFile(root, storageKey, out var fullPath))
        {
            return null;
        }

        // Milestone 6.4 (§6.3, backlog 4.1): derive a CHEAP weak validator from a single stat (length +
        // last-write-time) — NO read, NO rehash on every GET. The controller streams the path via
        // PhysicalFileResult, so a matching If-None-Match 304s before the file is ever opened. A byte change
        // alters the length or the write time, so a stale cache still revalidates.
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogServeReadFailed(_logger, ex, storageKey);
            return null;
        }

        var contentType = AvatarStorageKey.ContentTypeForKey(storageKey);
        var etag = $"W/\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        return new AvatarFile(fullPath, contentType, etag);
    }

    private void TryDeleteFile(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best-effort: a delete failure leaves an orphan file but never fails the caller (orphan-sweep
            // deferred, ADR 0013). A missing file is not an error (idempotent delete).
            LogDeleteFailed(_logger, ex, fullPath);
        }
    }

    private static ValidationException NotAnAllowedImage() =>
        new([new ValidationFailure("File", "The uploaded file must be a JPEG, PNG, or WebP image.")]);

    private static ValidationException TooLarge(long maxBytes) =>
        new([new ValidationFailure(
            "File",
            $"The uploaded file exceeds the maximum allowed size of {maxBytes} bytes.")]);

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Warning,
        Message = "Best-effort avatar delete failed for path {Path}; leaving an orphan file.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Resolving avatar metadata failed for key {StorageKey}; serving 404.")]
    private static partial void LogServeReadFailed(ILogger logger, Exception exception, string storageKey);
}
