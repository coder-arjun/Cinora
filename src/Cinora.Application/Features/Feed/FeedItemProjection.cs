using System.Linq.Expressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.ValueObjects;

namespace Cinora.Application.Features.Feed;

/// <summary>
/// The single translatable projection from a <see cref="Review"/> to a feed row, mirroring
/// <c>ReviewProjection</c> so the feed shares its N+1-free shape (§13). The aggregate <c>LikeCount</c>/
/// <c>CommentCount</c> and the per-viewer <c>LikedByMe</c> are correlated sub-queries INSIDE the one SQL
/// statement; the author (display name + avatar) and the title (TMDB coordinates + poster) are each resolved
/// by a single correlated scalar sub-query against the same context. The <see cref="Rating"/> value object is
/// materialized through its EF value converter here and unwrapped to its <see cref="int"/> in
/// <see cref="ToVm"/> (client-side) — selecting the converted property stays translatable, whereas reading
/// <c>Rating.Value</c> in the SQL projection is not guaranteed to be.
/// </summary>
internal static class FeedItemProjection
{
    /// <summary>The maximum length of the body excerpt shown on a feed card before truncation.</summary>
    public const int BodyExcerptMaxLength = 200;

    /// <summary>
    /// Builds the EF projection expression for one feed item, closing over the context (for the correlated
    /// count/like/author/title sub-queries) and the current user id (for <c>LikedByMe</c>). An anonymous viewer
    /// (<paramref name="currentUserId"/> is <c>null</c>) yields <c>LikedByMe = false</c> naturally.
    /// </summary>
    /// <param name="db">The persistence context whose sets back the correlated sub-queries.</param>
    /// <param name="currentUserId">The viewing user's id, or <c>null</c> when anonymous.</param>
    /// <returns>An <see cref="Expression"/> projecting a <see cref="Review"/> to a <see cref="FeedItemRow"/>.</returns>
    public static Expression<Func<Review, FeedItemRow>> ToRow(IAppDbContext db, Guid? currentUserId) =>
        review => new FeedItemRow
        {
            ReviewId = review.Id,
            AuthorUserId = review.UserId,
            Author = db.Users
                .Where(user => user.Id == review.UserId)
                .Select(user => new FeedAuthorRow
                {
                    DisplayName = user.DisplayName,
                    AvatarFileKey = user.AvatarFileKey,
                })
                .FirstOrDefault(),
            Movie = db.Movies
                .Where(movie => movie.Id == review.MovieId)
                .Select(movie => new FeedMovieRow
                {
                    TmdbId = movie.TmdbId,
                    MediaType = movie.MediaType,
                    Title = movie.Title,
                    PosterPath = movie.PosterPath,
                })
                .FirstOrDefault(),
            Rating = review.Rating,
            // Milestone 6.4 (§6.4, backlog 3.4): truncate the excerpt IN SQL so only ~201 chars cross the wire
            // per row, not the full nvarchar(4000) body. EF's SQL Server provider DOES translate
            // string.Substring(int,int)/.Length (as does SQLite → substr/length) — the old "not translatable"
            // comment was wrong. Fetch one MORE than BodyExcerptMaxLength so ToVm's Excerpt can still tell a body
            // that is exactly at the cap from one that overflows (and thus needs the trailing ellipsis).
            Body = review.Body.Length <= BodyExcerptMaxLength + 1
                ? review.Body
                : review.Body.Substring(0, BodyExcerptMaxLength + 1),
            CreatedAtUtc = review.CreatedAtUtc,
            LikeCount = db.ReviewLikes.Count(like => like.ReviewId == review.Id),
            CommentCount = db.Comments.Count(comment => comment.ReviewId == review.Id),
            LikedByMe = db.ReviewLikes.Any(like => like.ReviewId == review.Id && like.UserId == currentUserId),
        };

    /// <summary>
    /// Maps a materialized <see cref="FeedItemRow"/> to the public <see cref="FeedItemVm"/> in memory: unwraps
    /// the <see cref="Rating"/> value object to its <see cref="int"/> and truncates the body to a short excerpt.
    /// </summary>
    /// <param name="row">The materialized projection row.</param>
    /// <returns>The display view model.</returns>
    public static FeedItemVm ToVm(FeedItemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new FeedItemVm
        {
            ReviewId = row.ReviewId,
            AuthorUserId = row.AuthorUserId,
            AuthorDisplayName = row.Author?.DisplayName ?? string.Empty,
            AuthorAvatarFileKey = row.Author?.AvatarFileKey,
            MovieTmdbId = row.Movie?.TmdbId ?? 0,
            MovieMediaType = row.Movie?.MediaType ?? MediaType.Movie,
            MovieTitle = row.Movie?.Title ?? string.Empty,
            MoviePosterPath = row.Movie?.PosterPath,
            Rating = row.Rating.Value,
            BodyExcerpt = Excerpt(row.Body),
            CreatedAtUtc = row.CreatedAtUtc,
            LikeCount = row.LikeCount,
            CommentCount = row.CommentCount,
            LikedByMe = row.LikedByMe,
        };
    }

    // Finishes the excerpt in memory. SQL already truncated the body to at most BodyExcerptMaxLength + 1 chars
    // (see ToRow — the truncation is pushed into SUBSTRING/substr, which EF DOES translate), so this only trims
    // trailing whitespace and appends the ellipsis when the body actually overflowed the cap; a short body is
    // returned verbatim.
    private static string Excerpt(string body)
    {
        if (string.IsNullOrEmpty(body) || body.Length <= BodyExcerptMaxLength)
        {
            return body;
        }

        return string.Concat(body.AsSpan(0, BodyExcerptMaxLength).TrimEnd(), "…");
    }
}

/// <summary>
/// The intermediate shape EF materializes for a feed projection: it holds the <see cref="Rating"/> value object
/// (materialized through its converter) and the full body, so the SQL projection stays translatable;
/// <see cref="FeedItemProjection.ToVm"/> completes the mapping client-side.
/// </summary>
internal sealed class FeedItemRow
{
    /// <summary>The review's unique identifier.</summary>
    public required Guid ReviewId { get; init; }

    /// <summary>The author's user id.</summary>
    public required Guid AuthorUserId { get; init; }

    /// <summary>The author's display name + avatar, resolved by ONE correlated sub-query against <c>Users</c>.</summary>
    public FeedAuthorRow? Author { get; init; }

    /// <summary>The reviewed title's TMDB coordinates + poster, resolved by ONE correlated sub-query against <c>Movies</c>.</summary>
    public FeedMovieRow? Movie { get; init; }

    /// <summary>The rating value object (materialized through its EF value converter).</summary>
    public required Rating Rating { get; init; }

    /// <summary>The plain-text review body (truncated to an excerpt client-side).</summary>
    public required string Body { get; init; }

    /// <summary>The UTC creation instant.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The like count (a correlated sub-query).</summary>
    public required int LikeCount { get; init; }

    /// <summary>The comment count (a correlated sub-query).</summary>
    public required int CommentCount { get; init; }

    /// <summary>Whether the current viewer liked the review (a correlated <c>Any</c>).</summary>
    public required bool LikedByMe { get; init; }
}

/// <summary>The author fields materialized by the single correlated <c>Users</c> sub-query in the feed projection.</summary>
internal sealed class FeedAuthorRow
{
    /// <summary>The author's public display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The author's avatar storage key, or <c>null</c>.</summary>
    public string? AvatarFileKey { get; init; }
}

/// <summary>The title fields materialized by the single correlated <c>Movies</c> sub-query in the feed projection.</summary>
internal sealed class FeedMovieRow
{
    /// <summary>The reviewed title's TMDB identifier.</summary>
    public int TmdbId { get; init; }

    /// <summary>Whether the reviewed title is a movie or a series.</summary>
    public MediaType MediaType { get; init; }

    /// <summary>The reviewed title's display title.</summary>
    public string? Title { get; init; }

    /// <summary>The reviewed title's raw TMDB poster path, or <c>null</c>.</summary>
    public string? PosterPath { get; init; }
}
