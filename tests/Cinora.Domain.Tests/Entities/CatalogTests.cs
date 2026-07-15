using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Tests.Entities;

public class CatalogTests
{
    [Fact]
    public void Movie_FromTmdb_maps_metadata_and_assigns_an_internal_id()
    {
        var movie = Movie.FromTmdb(
            tmdbId: 603,
            mediaType: MediaType.Movie,
            title: "The Matrix",
            overview: "A hacker learns the truth.",
            releaseDate: new DateTime(1999, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            posterPath: "/poster.jpg",
            backdropPath: null);

        Assert.NotEqual(Guid.Empty, movie.Id);
        Assert.Equal(603, movie.TmdbId);
        Assert.Equal(MediaType.Movie, movie.MediaType);
        Assert.Equal("The Matrix", movie.Title);
        Assert.Null(movie.BackdropPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Movie_FromTmdb_with_non_positive_tmdb_id_throws_domain_exception(int tmdbId)
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Movie.FromTmdb(tmdbId, MediaType.Series, "Title", null, null, null, null);
        });
    }

    [Fact]
    public void Genre_Create_maps_the_tmdb_id_and_name()
    {
        var genre = Genre.Create(18, "Drama");

        Assert.Equal(18, genre.TmdbGenreId);
        Assert.Equal("Drama", genre.Name);
    }

    [Fact]
    public void MovieGenre_Link_holds_both_foreign_keys()
    {
        var movieId = Guid.NewGuid();
        var genreId = Guid.NewGuid();

        var link = MovieGenre.Link(movieId, genreId);

        Assert.Equal(movieId, link.MovieId);
        Assert.Equal(genreId, link.GenreId);
    }

    [Fact]
    public void Comment_Create_with_blank_body_throws_domain_exception()
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Comment.Create(Guid.NewGuid(), Guid.NewGuid(), "   ");
        });
    }

    [Fact]
    public void ReviewLike_Create_holds_both_foreign_keys()
    {
        var reviewId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var like = ReviewLike.Create(reviewId, userId);

        Assert.Equal(reviewId, like.ReviewId);
        Assert.Equal(userId, like.UserId);
    }

    [Fact]
    public void Device_Register_requires_an_endpoint()
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Device.Register(Guid.NewGuid(), "", "key", "secret");
        });
    }

    [Fact]
    public void AIRecommendationHistory_Create_rejects_negative_token_counts()
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = AIRecommendationHistory.Create(Guid.NewGuid(), "llama3.2", "in", "out", -1, 0);
        });
    }
}
