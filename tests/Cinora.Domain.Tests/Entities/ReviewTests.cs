using Cinora.Domain.Entities;
using Cinora.Domain.Exceptions;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Tests.Entities;

public class ReviewTests
{
    [Fact]
    public void Create_sets_the_supplied_values_and_leaves_updated_time_unset()
    {
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();

        var review = Review.Create(userId, movieId, Rating.From(8), "Loved it.");

        Assert.Equal(userId, review.UserId);
        Assert.Equal(movieId, review.MovieId);
        Assert.Equal(8, review.Rating.Value);
        Assert.Equal("Loved it.", review.Body);
        Assert.False(review.UpdatedAtUtc.HasValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Create_with_empty_or_whitespace_body_throws_domain_exception(string body)
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), body);
        });
    }

    [Fact]
    public void Create_with_body_exceeding_max_length_throws_domain_exception()
    {
        var tooLong = new string('x', Review.BodyMaxLength + 1);

        Assert.Throws<DomainException>(() =>
        {
            _ = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), tooLong);
        });
    }

    [Fact]
    public void Create_with_a_default_rating_throws_domain_exception()
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Review.Create(Guid.NewGuid(), Guid.NewGuid(), default, "Body");
        });
    }

    [Fact]
    public void Create_with_body_at_max_length_succeeds()
    {
        var body = new string('x', Review.BodyMaxLength);

        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), body);

        Assert.Equal(Review.BodyMaxLength, review.Body.Length);
    }

    [Fact]
    public void ChangeRating_updates_the_rating_and_stamps_updated_time()
    {
        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), "Body");

        review.ChangeRating(Rating.From(9));

        Assert.Equal(9, review.Rating.Value);
        Assert.True(review.UpdatedAtUtc.HasValue);
    }

    [Fact]
    public void ChangeRating_with_a_default_rating_throws_domain_exception()
    {
        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), "Body");

        Assert.Throws<DomainException>(() => review.ChangeRating(default));
    }

    [Fact]
    public void EditBody_updates_the_body_and_stamps_updated_time()
    {
        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), "Body");

        review.EditBody("A revised opinion.");

        Assert.Equal("A revised opinion.", review.Body);
        Assert.True(review.UpdatedAtUtc.HasValue);
    }

    [Fact]
    public void EditBody_with_empty_body_throws_domain_exception()
    {
        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Rating.From(5), "Body");

        Assert.Throws<DomainException>(() => review.EditBody("   "));
    }
}
