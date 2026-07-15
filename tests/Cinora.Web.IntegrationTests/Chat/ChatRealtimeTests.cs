using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Web.Hubs;
using Cinora.Web.Infrastructure;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C3.2 tests for the <see cref="SignalRChatNotifier"/> adapter: a push whose client invocation faults must
/// be swallowed (best-effort — a realtime failure never fails the already-committed write), and the strict CSP
/// stays byte-unchanged (chat is same-origin <c>wss://…/hubs/chat</c>, already admitted by <c>connect-src 'self'</c>
/// — no new host).
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ChatRealtimeTests
{
    private readonly CinoraWebApplicationFactory _factory;

    public ChatRealtimeTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task A_faulting_push_is_swallowed_best_effort()
    {
        var client = Substitute.For<IChatClient>();
        client.ReceiveMessage(Arg.Any<ChatMessageDto>()).Returns(Task.FromException(new InvalidOperationException("boom")));
        client.ConversationUpdated(Arg.Any<ConversationEventDto>()).Returns(Task.FromException(new InvalidOperationException("boom")));
        client.ConversationRead(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var clients = Substitute.For<IHubClients<IChatClient>>();
        clients.Group(Arg.Any<string>()).Returns(client);
        clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(client);

        var hub = Substitute.For<IHubContext<ChatHub, IChatClient>>();
        hub.Clients.Returns(clients);

        var notifier = new SignalRChatNotifier(hub, Substitute.For<IPushDispatch>(), Substitute.For<ILogger<SignalRChatNotifier>>());
        var conversationId = Guid.NewGuid();
        var message = new ChatMessageDto(Guid.NewGuid(), conversationId, Guid.NewGuid(), "Sender", "hi", DateTime.UtcNow);

        // None of these should throw despite the client faulting.
        await notifier.MessageSentAsync(conversationId, [Guid.NewGuid()], message, CancellationToken.None);
        await notifier.ConversationReadAsync(conversationId, Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None);
        await notifier.ConversationChangedAsync(
            [Guid.NewGuid()], new ConversationEventDto(conversationId, "created"), CancellationToken.None);
    }

    [Fact]
    public async Task The_csp_still_admits_only_same_origin_connections()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = Assert.Single(values!);
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        // Chat added NO connect-src host: the hub is same-origin, so no ws:/wss: host was widened in.
        Assert.DoesNotContain("wss://", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("/hubs/chat", csp, StringComparison.Ordinal);
    }
}
