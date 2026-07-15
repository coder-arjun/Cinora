using Microsoft.Extensions.Logging;

namespace Cinora.Application.Features.Catalog;

/// <summary>
/// Source-generated, high-performance log messages for the catalog persistence handlers. Using
/// <see cref="LoggerMessageAttribute"/> (CA1848) avoids allocations and evaluates arguments only when the
/// level is enabled. Defined in a dedicated non-generic partial type because the logging source generator
/// does not emit methods for generic containing types; the handlers pass their categorized
/// <see cref="ILogger"/> here so the log event carries the concrete handler as its category.
/// </summary>
internal static partial class CatalogLog
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Warning,
        Message = "Genre-insert race reconciled: a concurrent writer created one or more of the " +
                  "{StagedGenreCount} staged genre(s) first (unique TmdbGenreId index); reloaded the " +
                  "committed ids instead of failing. Logged so a DbUpdateException that is NOT actually a " +
                  "genre race stays observable rather than silently swallowed")]
    public static partial void GenreInsertRaceReconciled(ILogger logger, Exception exception, int stagedGenreCount);
}
