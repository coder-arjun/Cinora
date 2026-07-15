using System.Reflection;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// Loads recorded TMDB JSON fixtures embedded in this test assembly. Fixtures are representative TMDB
/// response shapes with no secrets, so the adapter tests can run offline with no key and no network.
/// </summary>
internal static class TmdbFixtures
{
    /// <summary>Reads the embedded fixture with the given file name (for example <c>movie_list.json</c>).</summary>
    /// <param name="fileName">The fixture file name under the <c>Fixtures</c> folder.</param>
    /// <returns>The fixture's JSON content.</returns>
    public static string Load(string fileName)
    {
        var assembly = typeof(TmdbFixtures).Assembly;

        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded TMDB fixture '{fileName}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded TMDB fixture stream '{resourceName}' was null.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
