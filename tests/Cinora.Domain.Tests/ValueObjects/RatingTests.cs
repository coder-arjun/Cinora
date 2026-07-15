using Cinora.Domain.Exceptions;
using Cinora.Domain.ValueObjects;

namespace Cinora.Domain.Tests.ValueObjects;

public class RatingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void From_within_range_creates_rating_with_that_value(int value)
    {
        var rating = Rating.From(value);

        Assert.Equal(value, rating.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void From_out_of_range_throws_domain_exception(int value)
    {
        Assert.Throws<DomainException>(() =>
        {
            _ = Rating.From(value);
        });
    }

    [Fact]
    public void Ratings_with_the_same_value_are_equal()
    {
        Assert.Equal(Rating.From(7), Rating.From(7));
        Assert.True(Rating.From(7) == Rating.From(7));
    }

    [Fact]
    public void Ratings_with_different_values_are_not_equal()
    {
        Assert.NotEqual(Rating.From(7), Rating.From(8));
        Assert.True(Rating.From(7) != Rating.From(8));
    }
}
