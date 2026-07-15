using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Web.Hubs;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C3.1 auth/membership tests for the <see cref="ChatHub"/>. A full SignalR client harness is not present,
/// so (per the plan) the anonymous-rejection is asserted at the transport (an unauthenticated
/// <c>/hubs/chat/negotiate</c> never succeeds — the fail-closed <c>[Authorize]</c> hub), and the participation
/// gate the hub relies on is asserted directly on <see cref="ChatMembership"/> against the real database.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ChatHubAuthTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ChatHubAuthTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Anonymous_negotiate_to_the_chat_hub_is_rejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        using var response = await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", content: null);

        // Fail-closed: the [Authorize] hub must never negotiate for an anonymous caller. Cookie auth challenges
        // with a login redirect; other schemes answer 401 — either way it is NOT a successful negotiate.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"Expected 401/redirect for an anonymous negotiate, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Chat_membership_is_true_for_a_participant_and_false_for_an_outsider()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C31");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C31");
        using var outsider = await TestAuthentication.RegisterAndSignInAsync(_factory, "Outsider C31");

        Guid conversationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            var friendship = Domain.Entities.Friend.Request(me.UserId, friend.UserId);
            friendship.Accept();
            db.Friends.Add(friendship);
            await db.SaveChangesAsync();

            var currentUser = Substitute.For<ICurrentUser>();
            currentUser.GetRequiredUserId().Returns(me.UserId);
            conversationId = await new Application.Features.Chat.StartDirectConversationCommandHandler(db, currentUser)
                .Handle(new Application.Features.Chat.StartDirectConversationCommand(friend.UserId), CancellationToken.None);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            var membership = new ChatMembership(db);

            Assert.True(await membership.IsMemberAsync(conversationId, me.UserId, CancellationToken.None));
            Assert.True(await membership.IsMemberAsync(conversationId, friend.UserId, CancellationToken.None));
            Assert.False(await membership.IsMemberAsync(conversationId, outsider.UserId, CancellationToken.None));
        }
    }
}
