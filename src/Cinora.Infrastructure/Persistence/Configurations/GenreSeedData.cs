namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>
/// The static TMDB genre reference data seeded via <c>HasData</c>. It is the union of TMDB's movie
/// and TV genre lists (Cinora catalogues both), keyed by hardcoded, deterministic <see cref="Guid"/>s
/// so migration snapshots stay stable across runs. Each id embeds the TMDB genre id in its first
/// segment for readability (for example genre 28 → <c>00000028-...</c>); the domain
/// <c>Genre.TmdbGenreId</c> unique index guarantees no duplicate TMDB ids.
/// </summary>
internal static class GenreSeedData
{
    /// <summary>The seed rows as anonymous objects (the domain <c>Genre</c> has a private constructor).</summary>
    internal static readonly object[] Genres =
    [
        // --- TMDB movie genres (/genre/movie/list) ---
        new { Id = new Guid("00000028-0000-0000-0000-000000000000"), TmdbGenreId = 28, Name = "Action" },
        new { Id = new Guid("00000012-0000-0000-0000-000000000000"), TmdbGenreId = 12, Name = "Adventure" },
        new { Id = new Guid("00000016-0000-0000-0000-000000000000"), TmdbGenreId = 16, Name = "Animation" },
        new { Id = new Guid("00000035-0000-0000-0000-000000000000"), TmdbGenreId = 35, Name = "Comedy" },
        new { Id = new Guid("00000080-0000-0000-0000-000000000000"), TmdbGenreId = 80, Name = "Crime" },
        new { Id = new Guid("00000099-0000-0000-0000-000000000000"), TmdbGenreId = 99, Name = "Documentary" },
        new { Id = new Guid("00000018-0000-0000-0000-000000000000"), TmdbGenreId = 18, Name = "Drama" },
        new { Id = new Guid("00010751-0000-0000-0000-000000000000"), TmdbGenreId = 10751, Name = "Family" },
        new { Id = new Guid("00000014-0000-0000-0000-000000000000"), TmdbGenreId = 14, Name = "Fantasy" },
        new { Id = new Guid("00000036-0000-0000-0000-000000000000"), TmdbGenreId = 36, Name = "History" },
        new { Id = new Guid("00000027-0000-0000-0000-000000000000"), TmdbGenreId = 27, Name = "Horror" },
        new { Id = new Guid("00010402-0000-0000-0000-000000000000"), TmdbGenreId = 10402, Name = "Music" },
        new { Id = new Guid("00009648-0000-0000-0000-000000000000"), TmdbGenreId = 9648, Name = "Mystery" },
        new { Id = new Guid("00010749-0000-0000-0000-000000000000"), TmdbGenreId = 10749, Name = "Romance" },
        new { Id = new Guid("00000878-0000-0000-0000-000000000000"), TmdbGenreId = 878, Name = "Science Fiction" },
        new { Id = new Guid("00010770-0000-0000-0000-000000000000"), TmdbGenreId = 10770, Name = "TV Movie" },
        new { Id = new Guid("00000053-0000-0000-0000-000000000000"), TmdbGenreId = 53, Name = "Thriller" },
        new { Id = new Guid("00010752-0000-0000-0000-000000000000"), TmdbGenreId = 10752, Name = "War" },
        new { Id = new Guid("00000037-0000-0000-0000-000000000000"), TmdbGenreId = 37, Name = "Western" },

        // --- TMDB TV-only genres (/genre/tv/list) not already present above ---
        new { Id = new Guid("00010759-0000-0000-0000-000000000000"), TmdbGenreId = 10759, Name = "Action & Adventure" },
        new { Id = new Guid("00010762-0000-0000-0000-000000000000"), TmdbGenreId = 10762, Name = "Kids" },
        new { Id = new Guid("00010763-0000-0000-0000-000000000000"), TmdbGenreId = 10763, Name = "News" },
        new { Id = new Guid("00010764-0000-0000-0000-000000000000"), TmdbGenreId = 10764, Name = "Reality" },
        new { Id = new Guid("00010765-0000-0000-0000-000000000000"), TmdbGenreId = 10765, Name = "Sci-Fi & Fantasy" },
        new { Id = new Guid("00010766-0000-0000-0000-000000000000"), TmdbGenreId = 10766, Name = "Soap" },
        new { Id = new Guid("00010767-0000-0000-0000-000000000000"), TmdbGenreId = 10767, Name = "Talk" },
        new { Id = new Guid("00010768-0000-0000-0000-000000000000"), TmdbGenreId = 10768, Name = "War & Politics" },
    ];
}
