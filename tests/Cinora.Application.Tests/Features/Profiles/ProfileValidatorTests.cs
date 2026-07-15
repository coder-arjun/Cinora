using Cinora.Application.Features.Profiles;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;

namespace Cinora.Application.Tests.Features.Profiles;

/// <summary>
/// Unit tests for the Milestone 4.2 profile command validators (the friendly-400 boundary — §4.1). They run the
/// FluentValidation rules in isolation, proving the display-name required/length rules mirror the domain
/// <c>User.Rename</c> / <c>Guard.Required</c> guard, and that the avatar validator enforces the declared
/// content-type allow-list and the size cap from the <see cref="Cinora.Application.Common.Interfaces.IUploadPolicy"/>
/// port BEFORE the stream is read. The authoritative guards stay on the domain and the storage adapter's
/// magic-byte gate respectively; these validators are defense-in-depth for a clean user-facing message.
/// </summary>
public sealed class ProfileValidatorTests
{
    private readonly UpdateProfileCommandValidator _updateValidator = new();
    private readonly ChangeAvatarCommandValidator _avatarValidator = new(new StubUploadPolicy(maxUploadBytes: 5_000_000));

    [Fact]
    public void Update_with_a_valid_name_passes()
    {
        var result = _updateValidator.Validate(new UpdateProfileCommand("Ada Lovelace", IsProfilePublic: true));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_with_a_blank_name_fails_on_the_display_name(string displayName)
    {
        var result = _updateValidator.Validate(new UpdateProfileCommand(displayName, IsProfilePublic: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(UpdateProfileCommand.DisplayName));
    }

    [Fact]
    public void Update_with_an_over_length_name_fails_on_the_display_name()
    {
        var name = new string('x', User.DisplayNameMaxLength + 1);

        var result = _updateValidator.Validate(new UpdateProfileCommand(name, IsProfilePublic: true));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(UpdateProfileCommand.DisplayName));
    }

    [Fact]
    public void Update_with_a_max_length_name_passes()
    {
        var name = new string('x', User.DisplayNameMaxLength);

        var result = _updateValidator.Validate(new UpdateProfileCommand(name, IsProfilePublic: true));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("image/jpeg; charset=utf-8")]
    public void Avatar_with_an_allowed_type_and_in_range_length_passes(string contentType)
    {
        var result = _avatarValidator.Validate(new ChangeAvatarCommand(Stream.Null, contentType, Length: 1_000));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public void Avatar_with_a_disallowed_type_fails_on_the_content_type(string contentType)
    {
        var result = _avatarValidator.Validate(new ChangeAvatarCommand(Stream.Null, contentType, Length: 1_000));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(ChangeAvatarCommand.ContentType));
    }

    [Fact]
    public void Avatar_with_a_zero_length_fails_on_the_length()
    {
        var result = _avatarValidator.Validate(new ChangeAvatarCommand(Stream.Null, "image/jpeg", Length: 0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(ChangeAvatarCommand.Length));
    }

    [Fact]
    public void Avatar_over_the_size_cap_fails_on_the_length()
    {
        var result = _avatarValidator.Validate(
            new ChangeAvatarCommand(Stream.Null, "image/jpeg", Length: 5_000_001));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(ChangeAvatarCommand.Length));
    }
}
