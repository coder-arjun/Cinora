using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Push;
using Cinora.Application.Features.Chat;
using Cinora.Application.Features.Push;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task (mobile push) tests for <see cref="SendChatPushCommand"/>: a new message pushes to the recipients'
/// registered Web Push devices with a <c>/chat/{id}</c> deep link, and never to the sender's own device.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ChatPushTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ChatPushTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_new_message_pushes_to_the_recipient_device_and_not_the_sender()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me Push");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend Push");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        await SeedDeviceAsync(friend.UserId, "https://push.example/friend");
        await SeedDeviceAsync(me.UserId, "https://push.example/me");

        var messageId = (await SendAsync(me.UserId, conversationId, "movie night?")).Id;

        var pushSender = Substitute.For<IPushSender>();
        pushSender.SendAsync(Arg.Any<PushSubscription>(), Arg.Any<PushPayload>(), Arg.Any<CancellationToken>())
            .Returns(PushSendResult.Delivered);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            await new SendChatPushCommandHandler(db, pushSender, Substitute.For<ILogger<SendChatPushCommandHandler>>())
                .Handle(new SendChatPushCommand(messageId), CancellationToken.None);
        }

        // Pushed to the friend (recipient) with the conversation deep link…
        await pushSender.Received(1).SendAsync(
            Arg.Is<PushSubscription>(s => s.Endpoint == "https://push.example/friend"),
            Arg.Is<PushPayload>(p => p.Url == $"/chat/{conversationId}" && p.Body == "movie night?"),
            Arg.Any<CancellationToken>());

        // …and NEVER to the sender's own device.
        await pushSender.DidNotReceive().SendAsync(
            Arg.Is<PushSubscription>(s => s.Endpoint == "https://push.example/me"),
            Arg.Any<PushPayload>(),
            Arg.Any<CancellationToken>());
    }

    private async Task<ChatMessageVm> SendAsync(Guid meUserId, Guid conversationId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        return await new SendMessageCommandHandler(db, currentUser, Substitute.For<IChatNotifier>())
            .Handle(new SendMessageCommand(conversationId, body), CancellationToken.None);
    }

    private async Task<Guid> StartDirectAsync(Guid meUserId, Guid otherUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        return await new StartDirectConversationCommandHandler(db, currentUser)
            .Handle(new StartDirectConversationCommand(otherUserId), CancellationToken.None);
    }

    private async Task SeedDeviceAsync(Guid userId, string endpoint)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.Devices.Add(Device.Register(userId, endpoint, "p256dh-key-value", "auth-secret-value"));
        await db.SaveChangesAsync();
    }

    private async Task SeedAcceptedFriendshipAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var friend = Friend.Request(a, b);
        friend.Accept();
        db.Friends.Add(friend);
        await db.SaveChangesAsync();
    }
}
