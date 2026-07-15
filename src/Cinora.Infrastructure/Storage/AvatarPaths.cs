using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Hosting;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// Resolves the on-disk location of avatar files (ADR 0013 §2.2). The upload root is
/// <see cref="FileStorageOptions.LocalRootPath"/> resolved relative to the host content root, deliberately
/// <b>OUTSIDE <c>wwwroot</c></b> so nothing under it is web-servable except through the token-gated media
/// controller. <see cref="TryResolveFile"/> combines the root with a storage key and performs a canonical
/// containment check as defense-in-depth: even though the key grammar (<see cref="AvatarStorageKey"/>) makes
/// <c>..</c> impossible, any resolved path that escapes the root is rejected rather than followed.
/// </summary>
internal static class AvatarPaths
{
    /// <summary>
    /// The absolute, canonical upload root: <c>{ContentRootPath}/{LocalRootPath}</c>. Callers create this
    /// directory at startup and combine keys against it via <see cref="TryResolveFile"/>.
    /// </summary>
    /// <param name="environment">The host environment supplying the content root.</param>
    /// <param name="options">The bound file-storage options supplying the relative root path.</param>
    /// <returns>The absolute upload-root path.</returns>
    public static string ResolveRoot(IHostEnvironment environment, FileStorageOptions options) =>
        Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.LocalRootPath));

    /// <summary>
    /// Resolves the absolute file path for a storage key under <paramref name="root"/>, validating the key
    /// grammar and asserting the canonical result stays strictly inside the root.
    /// </summary>
    /// <param name="root">The absolute upload root (from <see cref="ResolveRoot"/>).</param>
    /// <param name="storageKey">The storage key to resolve.</param>
    /// <param name="fullPath">The absolute file path when the method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the key is well-formed and resolves inside the root; <see langword="false"/>
    /// for a malformed / path-traversal-bearing key or any path that would escape the root.
    /// </returns>
    public static bool TryResolveFile(string root, string storageKey, out string fullPath)
    {
        fullPath = string.Empty;

        // First gate: the strict key grammar. A '..' or absolute segment cannot match, so this alone makes
        // traversal impossible; the canonical check below is belt-and-suspenders.
        if (!AvatarStorageKey.IsValid(storageKey))
        {
            return false;
        }

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, storageKey));

        // Canonical containment: the resolved path must live strictly under the root directory.
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
