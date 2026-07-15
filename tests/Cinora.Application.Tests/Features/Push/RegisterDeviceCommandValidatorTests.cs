using Cinora.Application.Features.Push;

namespace Cinora.Application.Tests.Features.Push;

/// <summary>
/// Milestone 6.2 folded security-Low: <see cref="RegisterDeviceCommandValidator"/> caps the endpoint + key lengths
/// at the persisted <c>Device</c> column bounds, so an oversized subscription is a friendly validation failure (a
/// 400 at the API boundary) rather than a truncation <c>SqlException</c> 500 downstream.
/// </summary>
public sealed class RegisterDeviceCommandValidatorTests
{
    private readonly RegisterDeviceCommandValidator _validator = new();

    [Fact]
    public void A_valid_subscription_passes()
    {
        var result = _validator.Validate(
            new RegisterDeviceCommand("https://fcm.googleapis.com/fcm/send/abc", "p256dh", "auth"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void An_oversized_endpoint_fails()
    {
        var endpoint = "https://push.example/" + new string('a', RegisterDeviceCommandValidator.EndpointMaxLength);

        var result = _validator.Validate(new RegisterDeviceCommand(endpoint, "p256dh", "auth"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterDeviceCommand.Endpoint));
    }

    [Fact]
    public void An_oversized_key_fails()
    {
        var key = new string('k', RegisterDeviceCommandValidator.KeyMaxLength + 1);

        var p256 = _validator.Validate(new RegisterDeviceCommand("https://push.example/x", key, "auth"));
        Assert.False(p256.IsValid);
        Assert.Contains(p256.Errors, e => e.PropertyName == nameof(RegisterDeviceCommand.P256dh));

        var auth = _validator.Validate(new RegisterDeviceCommand("https://push.example/x", "p256dh", key));
        Assert.False(auth.IsValid);
        Assert.Contains(auth.Errors, e => e.PropertyName == nameof(RegisterDeviceCommand.Auth));
    }

    [Fact]
    public void An_empty_field_fails()
    {
        var result = _validator.Validate(new RegisterDeviceCommand("", "p256dh", "auth"));

        Assert.False(result.IsValid);
    }
}
