using Cinora.Application.Features.Reviews;
using Cinora.Domain.Entities;

namespace Cinora.Application.Tests.Features.Reviews;

/// <summary>
/// Unit tests for the Milestone 3.1 review command validators (the friendly-400 boundary — §5.1). They run the
/// FluentValidation rules in isolation (no DbContext), proving the rating bounds and body constraints are
/// enforced before a command reaches its handler. The rating invariant itself stays authoritative in the
/// domain (<see cref="Cinora.Domain.ValueObjects.Rating"/>); these validators are defense-in-depth for a clean
/// user-facing message.
/// </summary>
public sealed class ReviewValidatorTests
{
    private readonly CreateReviewCommandValidator _createValidator = new();
    private readonly EditReviewCommandValidator _editValidator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Create_with_a_valid_rating_and_body_passes(int rating)
    {
        var result = _createValidator.Validate(new CreateReviewCommand(Guid.NewGuid(), rating, "A fine review."));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(11)]
    [InlineData(100)]
    public void Create_with_an_out_of_range_rating_fails_on_the_rating(int rating)
    {
        var result = _createValidator.Validate(new CreateReviewCommand(Guid.NewGuid(), rating, "A fine review."));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(CreateReviewCommand.Rating));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_with_a_blank_body_fails_on_the_body(string body)
    {
        var result = _createValidator.Validate(new CreateReviewCommand(Guid.NewGuid(), 7, body));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(CreateReviewCommand.Body));
    }

    [Fact]
    public void Create_with_an_over_length_body_fails_on_the_body()
    {
        var body = new string('x', Review.BodyMaxLength + 1);

        var result = _createValidator.Validate(new CreateReviewCommand(Guid.NewGuid(), 7, body));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(CreateReviewCommand.Body));
    }

    [Fact]
    public void Create_with_a_max_length_body_passes()
    {
        var body = new string('x', Review.BodyMaxLength);

        var result = _createValidator.Validate(new CreateReviewCommand(Guid.NewGuid(), 7, body));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Edit_with_an_out_of_range_rating_fails_on_the_rating(int rating)
    {
        var result = _editValidator.Validate(new EditReviewCommand(Guid.NewGuid(), rating, "An edited review."));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(EditReviewCommand.Rating));
    }

    [Fact]
    public void Edit_with_a_valid_rating_and_body_passes()
    {
        var result = _editValidator.Validate(new EditReviewCommand(Guid.NewGuid(), 8, "An edited review."));

        Assert.True(result.IsValid);
    }
}
