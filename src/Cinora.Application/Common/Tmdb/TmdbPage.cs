namespace Cinora.Application.Common.Tmdb;

/// <summary>
/// An Application-owned read model for one page of a TMDB paged result set (TMDB is page/offset based).
/// Returned by <see cref="Interfaces.ITmdbClient"/> search so callers can drive load-more pagination by
/// advancing <see cref="Page"/> toward <see cref="TotalPages"/>. TMDB's paged envelope is mapped to this
/// shape inside Infrastructure and never leaks outward (ADR 0006).
/// </summary>
/// <typeparam name="T">The Application read-model type of each item on the page.</typeparam>
public sealed record TmdbPage<T>
{
    /// <summary>The 1-based index of this page.</summary>
    public required int Page { get; init; }

    /// <summary>The total number of pages available for the query.</summary>
    public required int TotalPages { get; init; }

    /// <summary>The total number of results across all pages.</summary>
    public required int TotalResults { get; init; }

    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];
}
