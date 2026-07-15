using System.Text.Json;

namespace Cinora.Web.Infrastructure;

/// <summary>
/// Resolves a logical asset name (e.g. <c>app.css</c>) to its content-hashed, cache-busting URL
/// (e.g. <c>/dist/app-d67ae2e7ce.css</c>) using the <c>wwwroot/dist/manifest.json</c> emitted by the
/// npm build (<c>build.mjs</c>). Views consume it via <c>@inject</c> so the layout never hard-codes a
/// hashed filename. See <c>docs/architecture/solution-structure.md</c> §5.
/// </summary>
public interface IAssetManifest
{
    /// <summary>Resolves a logical asset key to its hashed, root-relative URL.</summary>
    /// <param name="logicalName">The logical asset key, e.g. <c>app.css</c> or <c>site.js</c>.</param>
    /// <returns>
    /// The root-relative URL of the built asset. Falls back to <c>/dist/{logicalName}</c> when the
    /// manifest or key is absent (e.g. before the first build) so a view renders a link rather than
    /// throwing.
    /// </returns>
    string Resolve(string logicalName);
}

/// <summary>
/// Default <see cref="IAssetManifest"/>. Reads and caches <c>wwwroot/dist/manifest.json</c> exactly once
/// (it is registered as a singleton); the hashed filenames are stable for the process lifetime.
/// </summary>
internal sealed partial class AssetManifest : IAssetManifest
{
    private const string ManifestPath = "dist/manifest.json";

    private readonly Lazy<Dictionary<string, string>> _entries;

    /// <summary>Initializes the manifest reader against the app's web root.</summary>
    /// <param name="environment">Supplies the web-root file provider used to read the manifest.</param>
    /// <param name="logger">Logs a warning when the manifest cannot be read.</param>
    public AssetManifest(IWebHostEnvironment environment, ILogger<AssetManifest> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _entries = new Lazy<Dictionary<string, string>>(
            () => Load(environment, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Resolve(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        return _entries.Value.TryGetValue(logicalName, out var hashedUrl)
            ? hashedUrl
            : $"/dist/{logicalName}";
    }

    private static Dictionary<string, string> Load(IWebHostEnvironment environment, ILogger logger)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(ManifestPath);
        if (!file.Exists)
        {
            LogManifestMissing(logger, ManifestPath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());

        return parsed is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Asset manifest '{ManifestPath}' was not found under the web root; asset URLs fall " +
                  "back to unhashed /dist paths. Run `npm run build` in src/Cinora.Web.")]
    private static partial void LogManifestMissing(ILogger logger, string manifestPath);
}
