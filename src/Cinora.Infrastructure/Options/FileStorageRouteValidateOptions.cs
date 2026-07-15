using Cinora.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Options;

/// <summary>
/// Fail-fast validation that the avatar <b>mint</b> side and <b>serve</b> side cannot silently drift (ADR 0013
/// §2.4). For the free <c>Local</c> provider the minted URL is built from
/// <see cref="FileStorageOptions.PublicBasePath"/> (<c>LocalFileStorage.GetUrl</c>) while the bytes are served by
/// Web's <c>MediaController</c> at <see cref="AvatarServingRoute.RouteBase"/>. If an operator changes
/// <c>PublicBasePath</c> so it no longer targets that route, every minted avatar URL would <c>404</c> with NO
/// boot error. This validator turns that silent config drift into a loud boot failure; it runs under
/// <c>ValidateOnStart</c> alongside <see cref="FileStorageProductionValidateOptions"/>. Only the local provider
/// is checked — a future Azure Blob adapter would mint a real SAS and not use the serving controller at all.
/// </summary>
internal sealed class FileStorageRouteValidateOptions : IValidateOptions<FileStorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Only the local filesystem provider serves through the MediaController route; other providers mint URLs
        // that do not depend on it, so there is nothing to keep in sync.
        if (!string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        // GetUrl builds "{PublicBasePath.TrimEnd('/')}/avatar"; the controller serves "/{RouteBase}/avatar". The
        // two seams only line up when PublicBasePath's single path segment IS the controller's route base.
        var configuredBase = (options.PublicBasePath ?? string.Empty).Trim('/');
        if (!string.Equals(configuredBase, AvatarServingRoute.RouteBase, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"'{FileStorageOptions.SectionName}:{nameof(FileStorageOptions.PublicBasePath)}' " +
                $"('{options.PublicBasePath}') must resolve to the '{AvatarServingRoute.PublicBasePath}' route " +
                "that MediaController serves avatars at (see AvatarServingRoute.RouteBase); otherwise every minted " +
                "avatar URL would 404. Change PublicBasePath and the controller route together.");
        }

        return ValidateOptionsResult.Success;
    }
}
