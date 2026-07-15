using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// The Infrastructure implementation of <see cref="IUploadPolicy"/> over <see cref="FileStorageOptions"/>. It
/// exposes the size cap and content-type allow-list to the Application-layer avatar validator without the
/// Application referencing this Options type (the dependency rule, ADR 0013 §2.3). This is only the fast,
/// friendly pre-check against the DECLARED content-type; <see cref="IFileStorage.SaveAsync"/> remains the
/// authoritative magic-byte gate.
/// </summary>
internal sealed class UploadPolicy : IUploadPolicy
{
    private readonly IOptions<FileStorageOptions> _options;

    /// <summary>Initializes the policy over the bound file-storage options.</summary>
    /// <param name="options">The file-storage options supplying the cap and allow-list.</param>
    public UploadPolicy(IOptions<FileStorageOptions> options) => _options = options;

    /// <inheritdoc />
    public long MaxUploadBytes => _options.Value.MaxUploadBytes;

    /// <inheritdoc />
    public bool IsAllowedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        // Normalize "image/jpeg; charset=..." → "image/jpeg" and compare case-insensitively.
        var mediaType = contentType.AsSpan();
        var separator = mediaType.IndexOf(';');
        if (separator >= 0)
        {
            mediaType = mediaType[..separator];
        }

        mediaType = mediaType.Trim();

        foreach (var allowed in _options.Value.AllowedContentTypes)
        {
            if (mediaType.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
