using System.ComponentModel.DataAnnotations;
using Cinora.Infrastructure.Storage;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Binds the <c>FileStorage</c> configuration section describing where user-uploaded media (avatars,
/// poster overrides) is stored behind the <c>IFileStorage</c> port. The free default provider is
/// <c>"Local"</c> — the local filesystem under <see cref="LocalRootPath"/>, served publicly at
/// <see cref="PublicBasePath"/>; the Azurite emulator is an optional dev alternative and Azure Blob a
/// future paid swap-in (CLAUDE.md free-only constraint). Upload guardrails (<see cref="MaxUploadBytes"/>
/// and <see cref="AllowedContentTypes"/>) live here so validators can enforce them. Bound with
/// <c>ValidateDataAnnotations</c> (lazy) but NOT <c>ValidateOnStart</c> in Phase 1; the consuming
/// <c>IFileStorage</c> implementation arrives in a later phase (solution-structure.md §6).
/// </summary>
public sealed class FileStorageOptions
{
    /// <summary>The configuration section name bound to this options type.</summary>
    public const string SectionName = "FileStorage";

    /// <summary>
    /// The storage provider name. Defaults to <c>"Local"</c> (free local filesystem). Selects which
    /// <c>IFileStorage</c> adapter the consuming phase resolves.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = "Local";

    /// <summary>
    /// The root path, relative to the content root, under which uploaded files are written for the local
    /// provider. Kept outside <c>wwwroot</c> so writes are not directly web-servable without an explicit route.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string LocalRootPath { get; set; } = "App_Data/uploads";

    /// <summary>
    /// The public URL base path at which stored files are served to clients. Defaults to the shared
    /// <see cref="AvatarServingRoute.PublicBasePath"/> (<c>/uploads</c>) so the minted-URL base and the
    /// <c>MediaController</c> route have a single source of truth; an override that no longer targets that route
    /// is a fail-fast boot error via <see cref="FileStorageRouteValidateOptions"/> (ADR 0013 §2.4).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string PublicBasePath { get; set; } = AvatarServingRoute.PublicBasePath;

    /// <summary>The maximum permitted upload size, in bytes. Defaults to roughly 5 MB.</summary>
    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; set; } = 5_000_000;

    /// <summary>
    /// The MIME content types accepted for uploads. Defaults to the common web image types
    /// (JPEG, PNG, WebP). Uploads whose content type is not in this list are rejected by validation.
    /// </summary>
    [MinLength(1)]
    public string[] AllowedContentTypes { get; set; } = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>
    /// The HMAC-SHA256 key used to sign and verify the time-limited avatar-serving URLs (the SAS stand-in,
    /// ADR 0013 §2.4). It is a SECRET: supplied via <c>dotnet user-secrets</c> / an environment variable
    /// (<c>FileStorage:UrlSigningKey</c>) in Production — never committed to <c>appsettings*.json</c> — and
    /// <b>required in Production</b> (enforced by <see cref="FileStorageProductionValidateOptions"/> at boot).
    /// In Development / Testing an ephemeral random key is auto-generated when this is blank, so local dev and
    /// the test suite work without a configured secret (ephemeral tokens simply invalidate on restart). Not
    /// annotated <c>[Required]</c> precisely because the blank-in-dev case is filled in by <c>PostConfigure</c>.
    /// </summary>
    public string UrlSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// The coarse expiry bucket, in hours, that avatar-serving URLs are rounded up to (ADR 0013 §2.4). A larger
    /// bucket makes a key's URL byte-identical (and browser-cacheable) for longer, at the cost of a replaced
    /// avatar remaining reachable via an old URL until the bucket rolls. Defaults to 24 hours.
    /// </summary>
    [Range(1, 168)]
    public int SignedUrlBucketHours { get; set; } = 24;
}
