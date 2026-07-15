using Cinora.Application.Features.Recommendations;

namespace Cinora.Application.Tests.Features.Recommendations;

/// <summary>
/// Unit tests for <see cref="GenerateRecommendationsCommandValidator"/> — the house-rule contract floor that the
/// generate command targets a real user (a defaulted <see cref="System.Guid.Empty"/> never reaches the handler).
/// Run in isolation (no DbContext, no pipeline).
/// </summary>
public sealed class GenerateRecommendationsCommandValidatorTests
{
    private readonly GenerateRecommendationsCommandValidator _validator = new();

    [Fact]
    public void An_empty_user_id_fails_on_the_user_id()
    {
        var result = _validator.Validate(new GenerateRecommendationsCommand(Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(GenerateRecommendationsCommand.UserId));
    }

    [Fact]
    public void A_real_user_id_passes()
    {
        var result = _validator.Validate(new GenerateRecommendationsCommand(Guid.NewGuid()));

        Assert.True(result.IsValid);
    }
}
