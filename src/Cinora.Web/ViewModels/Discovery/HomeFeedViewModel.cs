using Cinora.Application.Features.Discovery;
using Cinora.Domain.Enums;

namespace Cinora.Web.ViewModels.Discovery;

/// <summary>
/// The STATIC presentation config for the Home / discovery shell (Milestone 2.3). It carries only which
/// rails to render, in what order, under what heading — no TMDB data and no Domain entities. It lives in
/// Web (not Application) because it is UI copy plus the rail layout: the controller returns
/// <see cref="Default"/> and the view lazy-loads each <see cref="RailSlot"/> via HTMX from
/// <c>/discover/rail</c>, so this model never touches <c>ISender</c>, TMDB, or the database.
/// </summary>
/// <param name="Rails">The ordered rail slots the shell renders as lazy-loading containers.</param>
/// <param name="Language">The effective original-language filter (lower-case ISO-639-1); <c>null</c> = Global.</param>
public sealed record HomeFeedViewModel(IReadOnlyList<RailSlot> Rails, string? Language)
{
    // Movie rails then Series rails (each lazy-loaded independently). The language filter reuses these slots.
    private static readonly IReadOnlyList<RailSlot> DefaultRails =
    [
        new RailSlot(RailKind.Trending, MediaType.Movie, "Trending Movies"),
        new RailSlot(RailKind.Popular, MediaType.Movie, "Popular Movies"),
        new RailSlot(RailKind.TopRated, MediaType.Movie, "Top Rated Movies"),
        new RailSlot(RailKind.Trending, MediaType.Series, "Trending Series"),
        new RailSlot(RailKind.Popular, MediaType.Series, "Popular Series"),
        new RailSlot(RailKind.TopRated, MediaType.Series, "Top Rated Series"),
    ];

    /// <summary>The default Global shell (no original-language filter).</summary>
    public static HomeFeedViewModel Default { get; } = new(DefaultRails, null);

    /// <summary>Builds the shell for a specific effective language (<c>null</c>/blank = Global).</summary>
    /// <param name="language">The effective language code, or <c>null</c>/blank for Global.</param>
    /// <returns>The shell view model carrying the default rails and the effective language.</returns>
    public static HomeFeedViewModel ForLanguage(string? language) =>
        new(DefaultRails, string.IsNullOrWhiteSpace(language) ? null : language);
}

/// <summary>One rail slot in the Home shell: which curated rail, for which media type, under what heading.</summary>
/// <param name="Kind">The curated TMDB rail this slot renders.</param>
/// <param name="Media">The media type (Movie or Series) the rail is for.</param>
/// <param name="Heading">The visible heading shown in the shell before the cards stream in.</param>
public sealed record RailSlot(RailKind Kind, MediaType Media, string Heading);
