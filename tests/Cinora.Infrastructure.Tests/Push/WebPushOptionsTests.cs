using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Tests.Push;

/// <summary>
/// Milestone 6.2 tests for <see cref="WebPushOptions"/> (ADR 0020): the <see cref="WebPushOptions.IsConfigured"/>
/// gate (push is ON only when subject + public + private key are all present) and the FORMAT-if-present data
/// annotations validated through the SAME in-house validator the composition root wires
/// (<c>ValidateUsingDataAnnotations()</c>). Absent values are valid (degrade-if-absent — an unconfigured VAPID
/// set must not fail validation, so the app still boots with push off).
/// </summary>
public sealed class WebPushOptionsTests
{
    private const string ValidPublicKey = "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkFbcAoATHmGJj";
    private const string ValidPrivateKey = "3KzvKasA2SoCxsp0iIG_o5A_ntQwPHQFhCcQaW_KZ7A";

    private static ValidateOptionsResult Validate(WebPushOptions options) =>
        new DataAnnotationsValidateOptions<WebPushOptions>(name: null).Validate(name: string.Empty, options);

    [Fact]
    public void IsConfigured_is_true_only_when_all_three_credentials_are_present()
    {
        var configured = new WebPushOptions
        {
            Subject = "mailto:ops@cinora.test",
            PublicKey = ValidPublicKey,
            PrivateKey = ValidPrivateKey,
        };
        Assert.True(configured.IsConfigured);
    }

    [Theory]
    [InlineData(null, ValidPublicKey, ValidPrivateKey)]
    [InlineData("mailto:ops@cinora.test", null, ValidPrivateKey)]
    [InlineData("mailto:ops@cinora.test", ValidPublicKey, null)]
    [InlineData(null, null, null)]
    public void IsConfigured_is_false_when_any_credential_is_missing(string? subject, string? publicKey, string? privateKey)
    {
        var options = new WebPushOptions { Subject = subject, PublicKey = publicKey, PrivateKey = privateKey };
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Absent_configuration_passes_validation_degrade_if_absent()
    {
        Assert.True(Validate(new WebPushOptions()).Succeeded);
    }

    [Fact]
    public void Well_formed_configuration_passes_validation()
    {
        var options = new WebPushOptions
        {
            Subject = "https://cinora.test",
            PublicKey = ValidPublicKey,
            PrivateKey = ValidPrivateKey,
        };
        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData("ops@cinora.test")] // neither mailto: nor https://
    [InlineData("http://cinora.test")] // http is not allowed (must be https or mailto)
    public void Malformed_subject_fails_validation(string subject)
    {
        Assert.True(Validate(new WebPushOptions { Subject = subject }).Failed);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("plus+slash/chars")] // standard base64, not URL-safe base64url
    public void Malformed_public_key_fails_validation(string publicKey)
    {
        Assert.True(Validate(new WebPushOptions { PublicKey = publicKey }).Failed);
    }
}
