using Cinora.Application.Features.Comments;
using Cinora.Domain.Entities;

namespace Cinora.Application.Tests.Features.Comments;

/// <summary>
/// Unit tests for <see cref="AddCommentCommandValidator"/> (Milestone 3.2 — the friendly-400 boundary, §6.1):
/// a comment body is required and bounded to <see cref="Comment.BodyMaxLength"/>. Run in isolation (no DbContext).
/// </summary>
public sealed class AddCommentValidatorTests
{
    private readonly AddCommentCommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_body_fails_on_the_body(string body)
    {
        var result = _validator.Validate(new AddCommentCommand(Guid.NewGuid(), body));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(AddCommentCommand.Body));
    }

    [Fact]
    public void An_over_length_body_fails_on_the_body()
    {
        var body = new string('x', Comment.BodyMaxLength + 1);

        var result = _validator.Validate(new AddCommentCommand(Guid.NewGuid(), body));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(AddCommentCommand.Body));
    }

    [Fact]
    public void A_valid_body_passes()
    {
        var result = _validator.Validate(new AddCommentCommand(Guid.NewGuid(), "A thoughtful comment."));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_max_length_body_passes()
    {
        var body = new string('x', Comment.BodyMaxLength);

        var result = _validator.Validate(new AddCommentCommand(Guid.NewGuid(), body));

        Assert.True(result.IsValid);
    }
}
