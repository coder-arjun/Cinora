namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// Surfaces the upload guardrails (the content-type allow-list and the size cap) to the Application-layer
/// FluentValidation validator <b>without</b> the Application referencing Infrastructure's
/// <c>FileStorageOptions</c> — preserving the dependency rule (ADR 0001/0013). The validator uses these for the
/// fast, friendly pre-check (declared content-type ∈ allow-list, declared length ≤ cap) that produces a 400
/// before the stream is read; <see cref="IFileStorage.SaveAsync"/> remains the authoritative, magic-byte gate.
/// </summary>
public interface IUploadPolicy
{
    /// <summary>The maximum permitted upload size, in bytes.</summary>
    long MaxUploadBytes { get; }

    /// <summary>
    /// Whether the supplied (client-declared) content-type is in the accepted allow-list. Comparison is
    /// case-insensitive and tolerant of a trailing media-type parameter (e.g. <c>image/jpeg; charset=…</c>).
    /// </summary>
    /// <param name="contentType">The client-declared content-type to check.</param>
    /// <returns><see langword="true"/> when the media type is allowed; otherwise <see langword="false"/>.</returns>
    bool IsAllowedContentType(string? contentType);
}
