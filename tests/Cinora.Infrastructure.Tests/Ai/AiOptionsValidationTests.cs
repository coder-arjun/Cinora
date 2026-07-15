using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Tests.Ai;

/// <summary>
/// Verifies the data-annotation rules on <see cref="AiOptions"/> through the SAME validator the composition
/// root wires (<c>ValidateUsingDataAnnotations()</c> → the in-house <see cref="DataAnnotationsValidateOptions{TOptions}"/>).
/// Milestone 5.0 switches <c>ValidateOnStart</c> ON for <see cref="AiOptions"/>, so these annotations are exactly
/// what fails fast at boot: the free local Ollama defaults pass; a blank/non-URL endpoint, a blank model, or an
/// out-of-range timeout/output cap fail.
/// </summary>
public sealed class AiOptionsValidationTests
{
    // The validator is constructed with name: null so it validates regardless of the options name.
    private static ValidateOptionsResult Validate(AiOptions options) =>
        new DataAnnotationsValidateOptions<AiOptions>(name: null).Validate(name: string.Empty, options);

    [Fact]
    public void Default_ollama_options_are_valid()
    {
        Assert.True(Validate(new AiOptions()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void Missing_or_non_url_endpoint_is_invalid(string endpoint)
    {
        Assert.True(Validate(new AiOptions { Endpoint = endpoint }).Failed);
    }

    [Fact]
    public void Blank_provider_is_invalid()
    {
        Assert.True(Validate(new AiOptions { Provider = "" }).Failed);
    }

    [Fact]
    public void Blank_model_is_invalid()
    {
        Assert.True(Validate(new AiOptions { Model = "" }).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Out_of_range_timeout_is_invalid(int timeoutSeconds)
    {
        Assert.True(Validate(new AiOptions { TimeoutSeconds = timeoutSeconds }).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32_001)]
    public void Out_of_range_max_output_tokens_is_invalid(int maxOutputTokens)
    {
        Assert.True(Validate(new AiOptions { MaxOutputTokens = maxOutputTokens }).Failed);
    }
}
