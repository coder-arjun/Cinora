using System.Net;
using Cinora.Application.Common.Push;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Push;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Infrastructure.Tests.Push;

/// <summary>
/// Milestone 6.2 tests for <see cref="WebPushSender"/> (ADR 0020 §3.2): the push-service status → result mapping
/// (404/410 prune the subscription, anything else is transient) and the degrade-if-absent posture (with no VAPID
/// configured, a send is a no-op that returns <see cref="PushSendResult.TransientFailure"/> without a network
/// call or a throw). The real RFC-8291/8292 round-trip is a carried manual browser smoke (needs live VAPID keys
/// + a push service), listed in the milestone report.
/// </summary>
public sealed class WebPushSenderTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound, PushSendResult.Gone)]
    [InlineData(HttpStatusCode.Gone, PushSendResult.Gone)]
    [InlineData(HttpStatusCode.InternalServerError, PushSendResult.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, PushSendResult.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, PushSendResult.TransientFailure)]
    public void Classify_maps_gone_statuses_to_prune_and_the_rest_to_transient(
        HttpStatusCode statusCode, PushSendResult expected)
    {
        Assert.Equal(expected, WebPushSender.Classify(statusCode));
    }

    [Fact]
    public async Task SendAsync_with_unconfigured_options_is_a_noop_transient_failure()
    {
        using var sender = new WebPushSender(
            Microsoft.Extensions.Options.Options.Create(new WebPushOptions()),
            NullLogger<WebPushSender>.Instance);

        var result = await sender.SendAsync(
            new PushSubscription("https://push.example/1", "p256dh", "auth"),
            new PushPayload("Cinora", "Body", "/friends", "FriendRequest"),
            CancellationToken.None);

        Assert.Equal(PushSendResult.TransientFailure, result);
    }
}
