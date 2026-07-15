using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Chat;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C2.2 tests for <see cref="StartDirectConversationCommand"/> (find-or-create, friend + block gate).
/// Users are seeded through <see cref="TestAuthentication.RegisterAndSignInAsync"/> so the real Identity + domain
/// <c>User</c> rows exist (the chat FKs to <c>Users</c> reject arbitrary GUIDs). Each command is driven by
/// constructing the handler directly against a scoped <see cref="CinoraDbContext"/> with an NSubstitute
/// <see cref="ICurrentUser"/> standing in for the acting user — outside an HTTP request there is no populated
/// <c>IHttpContextAccessor</c> for the real <c>ICurrentUser</c> to read (mirrors <c>BlockingTests</c>).
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class StartDirectTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public StartDirectTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Starting_a_chat_with_a_non_friend_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C22a");
        using var stranger = await TestAuthentication.RegisterAndSignInAsync(_factory, "Stranger C22a");

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => StartDirectAsync(me.UserId, stranger.UserId));
    }

    [Fact]
    public async Task Starting_a_chat_with_a_friend_creates_a_direct_conversation_with_two_members()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C22b");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C22b");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);

        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.True(await db.Conversations.AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.Type == ConversationType.Direct));
        var memberIds = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.UserId)
            .ToListAsync();
        Assert.Equal(2, memberIds.Count);
        Assert.Contains(me.UserId, memberIds);
        Assert.Contains(friend.UserId, memberIds);
    }

    [Fact]
    public async Task Starting_a_chat_twice_returns_the_same_conversation()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C22c");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C22c");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);

        var first = await StartDirectAsync(me.UserId, friend.UserId);
        var second = await StartDirectAsync(friend.UserId, me.UserId); // opposite direction, same pair

        Assert.Equal(first, second);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.Equal(1, await db.Conversations.AsNoTracking()
            .CountAsync(c => c.Id == first));
    }

    [Fact]
    public async Task Starting_a_chat_with_a_blocked_friend_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C22d");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C22d");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        await SeedBlockAsync(me.UserId, friend.UserId);

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => StartDirectAsync(me.UserId, friend.UserId));
    }

    // ---- handler-driven helpers (constructs the handler directly; no ISender / HTTP context needed) ----

    private async Task<Guid> StartDirectAsync(Guid meUserId, Guid otherUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        var handler = new StartDirectConversationCommandHandler(db, currentUser);
        return await handler.Handle(new StartDirectConversationCommand(otherUserId), CancellationToken.None);
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

    private async Task SeedBlockAsync(Guid blocker, Guid blocked)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.UserBlocks.Add(UserBlock.Create(blocker, blocked));
        await db.SaveChangesAsync();
    }
}
