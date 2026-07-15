using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
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
/// Task C2.4 tests for the group / read / membership / delete verticals: create-group (friends-only, creator =
/// admin), the conversation list + unread count, mark-read, admin-leave promotion, admin-only remove/rename, and
/// sender-only message delete. Handlers are driven by direct construction with an NSubstitute
/// <see cref="ICurrentUser"/> and <see cref="IChatNotifier"/> (mirrors <c>BlockingTests</c>); users are seeded
/// through registration so the chat FKs to <c>Users</c> are satisfied.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class GroupAndReadTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public GroupAndReadTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_a_group_with_friends_makes_the_creator_the_admin()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24a");
        using var friendA = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend A C24a");
        using var friendB = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend B C24a");
        await SeedAcceptedFriendshipAsync(me.UserId, friendA.UserId);
        await SeedAcceptedFriendshipAsync(me.UserId, friendB.UserId);

        var groupId = await CreateGroupAsync(me.UserId, "Movie Buddies", [friendA.UserId, friendB.UserId]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var myRole = await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == groupId && m.UserId == me.UserId)
            .Select(m => m.Role).FirstAsync();
        Assert.Equal(ConversationRole.Admin, myRole);
        Assert.Equal(3, await db.ConversationMembers.AsNoTracking().CountAsync(m => m.ConversationId == groupId));
        Assert.Equal(ConversationType.Group, await db.Conversations.AsNoTracking()
            .Where(c => c.Id == groupId).Select(c => c.Type).FirstAsync());
    }

    [Fact]
    public async Task Creating_a_group_with_a_non_friend_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24b");
        using var stranger = await TestAuthentication.RegisterAndSignInAsync(_factory, "Stranger C24b");

        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            CreateGroupAsync(me.UserId, "Sneaky", [stranger.UserId]));
    }

    [Fact]
    public async Task Conversation_list_is_newest_first_with_the_right_unread_count()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24c");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C24c");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        var notifier = Substitute.For<IChatNotifier>();
        await SendAsync(friend.UserId, conversationId, "hi", notifier);
        await SendAsync(friend.UserId, conversationId, "you there?", notifier);

        var list = await GetConversationsAsync(me.UserId);

        var row = Assert.Single(list.Conversations);
        Assert.Equal(conversationId, row.Id);
        Assert.Equal(ConversationType.Direct, row.Type);
        Assert.Equal(friend.DisplayName, row.Title); // DM title = the OTHER member's name
        Assert.Equal("you there?", row.LastMessagePreview);
        Assert.Equal(2, row.UnreadCount); // both from the friend, unread by me
    }

    [Fact]
    public async Task Marking_read_zeroes_the_unread_count_and_pushes_a_receipt()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24d");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C24d");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);
        await SendAsync(friend.UserId, conversationId, "unread!", Substitute.For<IChatNotifier>());

        Assert.Equal(1, (await GetConversationsAsync(me.UserId)).Conversations.Single().UnreadCount);

        var notifier = Substitute.For<IChatNotifier>();
        await MarkReadAsync(me.UserId, conversationId, notifier);

        Assert.Equal(0, (await GetConversationsAsync(me.UserId)).Conversations.Single().UnreadCount);
        await notifier.Received(1).ConversationReadAsync(
            conversationId, me.UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_admin_leaves_the_earliest_joined_member_is_promoted()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24e");
        using var earliest = await TestAuthentication.RegisterAndSignInAsync(_factory, "Earliest C24e");
        using var latest = await TestAuthentication.RegisterAndSignInAsync(_factory, "Latest C24e");
        await SeedAcceptedFriendshipAsync(me.UserId, earliest.UserId);
        await SeedAcceptedFriendshipAsync(me.UserId, latest.UserId);

        // Create with 'earliest' (joins with the creator), then add 'latest' later so its JoinedAtUtc is strictly
        // greater — making the promotion target unambiguous.
        var groupId = await CreateGroupAsync(me.UserId, "Crew", [earliest.UserId]);
        await AddMembersAsync(me.UserId, groupId, [latest.UserId]);

        await LeaveAsync(me.UserId, groupId, Substitute.For<IChatNotifier>());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.False(await db.ConversationMembers.AsNoTracking()
            .AnyAsync(m => m.ConversationId == groupId && m.UserId == me.UserId)); // I left
        Assert.Equal(ConversationRole.Admin, await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == groupId && m.UserId == earliest.UserId).Select(m => m.Role).FirstAsync());
        Assert.Equal(ConversationRole.Member, await db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == groupId && m.UserId == latest.UserId).Select(m => m.Role).FirstAsync());
    }

    [Fact]
    public async Task Removing_a_member_is_admin_only()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24f");
        using var friendA = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend A C24f");
        using var friendB = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend B C24f");
        await SeedAcceptedFriendshipAsync(me.UserId, friendA.UserId);
        await SeedAcceptedFriendshipAsync(me.UserId, friendB.UserId);
        var groupId = await CreateGroupAsync(me.UserId, "Crew", [friendA.UserId, friendB.UserId]);

        // friendA is an ordinary member — they cannot remove friendB.
        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            RemoveMemberAsync(friendA.UserId, groupId, friendB.UserId, Substitute.For<IChatNotifier>()));
    }

    [Fact]
    public async Task Renaming_a_group_is_admin_only()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24g");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C24g");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var groupId = await CreateGroupAsync(me.UserId, "Old Name", [friend.UserId]);

        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            RenameAsync(friend.UserId, groupId, "Hijacked", Substitute.For<IChatNotifier>()));

        // The admin can rename.
        await RenameAsync(me.UserId, groupId, "New Name", Substitute.For<IChatNotifier>());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.Equal("New Name", await db.Conversations.AsNoTracking()
            .Where(c => c.Id == groupId).Select(c => c.Title).FirstAsync());
    }

    [Fact]
    public async Task Deleting_a_message_is_sender_only_and_soft()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24h");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C24h");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);
        var sent = await SendAsync(friend.UserId, conversationId, "the friend's message", Substitute.For<IChatNotifier>());

        // I did not send it — I cannot delete it.
        await Assert.ThrowsAsync<ForbiddenAccessException>(() => DeleteMessageAsync(me.UserId, sent.Id));

        // The sender can; the row is kept and tombstoned.
        await DeleteMessageAsync(friend.UserId, sent.Id);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.True(await db.Messages.AsNoTracking().Where(m => m.Id == sent.Id).Select(m => m.IsDeleted).FirstAsync());
    }

    [Fact]
    public async Task After_removing_a_member_a_sent_message_is_not_pushed_to_them()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24i");
        using var stay = await TestAuthentication.RegisterAndSignInAsync(_factory, "Stay C24i");
        using var removed = await TestAuthentication.RegisterAndSignInAsync(_factory, "Removed C24i");
        await SeedAcceptedFriendshipAsync(me.UserId, stay.UserId);
        await SeedAcceptedFriendshipAsync(me.UserId, removed.UserId);
        var groupId = await CreateGroupAsync(me.UserId, "Crew", [stay.UserId, removed.UserId]);

        await RemoveMemberAsync(me.UserId, groupId, removed.UserId, Substitute.For<IChatNotifier>());

        // The realtime fan-out must address only CURRENT members — the removed user is never a recipient (H1).
        var notifier = Substitute.For<IChatNotifier>();
        await SendAsync(me.UserId, groupId, "after removal", notifier);

        await notifier.Received(1).MessageSentAsync(
            groupId,
            Arg.Is<IReadOnlyList<Guid>>(ids =>
                !ids.Contains(removed.UserId) && ids.Contains(me.UserId) && ids.Contains(stay.UserId)),
            Arg.Any<ChatMessageDto>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task My_message_becomes_read_by_others_only_after_the_recipient_reads()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24k");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C24k");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);
        var sent = await SendAsync(me.UserId, conversationId, "seen yet?", Substitute.For<IChatNotifier>());

        // Before the friend reads: not read (single tick).
        var before = await GetMessagesAsync(me.UserId, conversationId);
        Assert.False(before.Messages.Single(m => m.Id == sent.Id).IsReadByOthers);

        await MarkReadAsync(friend.UserId, conversationId, Substitute.For<IChatNotifier>());

        // After the friend reads: read by others (blue double-tick).
        var after = await GetMessagesAsync(me.UserId, conversationId);
        Assert.True(after.Messages.Single(m => m.Id == sent.Id).IsReadByOthers);
    }

    [Fact]
    public async Task A_newly_added_member_cannot_read_messages_sent_before_they_joined()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C24j");
        using var early = await TestAuthentication.RegisterAndSignInAsync(_factory, "Early C24j");
        using var late = await TestAuthentication.RegisterAndSignInAsync(_factory, "Late C24j");
        await SeedAcceptedFriendshipAsync(me.UserId, early.UserId);
        await SeedAcceptedFriendshipAsync(me.UserId, late.UserId);
        var groupId = await CreateGroupAsync(me.UserId, "Crew", [early.UserId]);

        await SendAsync(me.UserId, groupId, "secret before late joined", Substitute.For<IChatNotifier>());
        await AddMembersAsync(me.UserId, groupId, [late.UserId]);
        await SendAsync(late.UserId, groupId, "hi from late", Substitute.For<IChatNotifier>());

        var thread = await GetMessagesAsync(late.UserId, groupId);

        Assert.DoesNotContain(thread.Messages, m => m.Body == "secret before late joined"); // pre-join, hidden
        Assert.Contains(thread.Messages, m => m.Body == "hi from late"); // post-join, visible
    }

    // ---- handler-driven helpers ----

    private async Task<Guid> StartDirectAsync(Guid meUserId, Guid otherUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = CurrentUser(meUserId);
        return await new StartDirectConversationCommandHandler(db, currentUser)
            .Handle(new StartDirectConversationCommand(otherUserId), CancellationToken.None);
    }

    private async Task<ChatMessageVm> SendAsync(Guid meUserId, Guid conversationId, string body, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await new SendMessageCommandHandler(db, CurrentUser(meUserId), notifier)
            .Handle(new SendMessageCommand(conversationId, body), CancellationToken.None);
    }

    private async Task<Guid> CreateGroupAsync(Guid meUserId, string title, IReadOnlyList<Guid> memberIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await new CreateGroupConversationCommandHandler(db, CurrentUser(meUserId), Substitute.For<IChatNotifier>())
            .Handle(new CreateGroupConversationCommand(title, memberIds), CancellationToken.None);
    }

    private async Task AddMembersAsync(Guid meUserId, Guid conversationId, IReadOnlyList<Guid> memberIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new AddGroupMembersCommandHandler(db, CurrentUser(meUserId), Substitute.For<IChatNotifier>())
            .Handle(new AddGroupMembersCommand(conversationId, memberIds), CancellationToken.None);
    }

    private async Task<ConversationsVm> GetConversationsAsync(Guid meUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await new GetConversationsQueryHandler(db, CurrentUser(meUserId))
            .Handle(new GetConversationsQuery(), CancellationToken.None);
    }

    private async Task<MessageThreadVm> GetMessagesAsync(Guid meUserId, Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await new GetMessagesQueryHandler(db, CurrentUser(meUserId))
            .Handle(new GetMessagesQuery(conversationId, null, null), CancellationToken.None);
    }

    private async Task MarkReadAsync(Guid meUserId, Guid conversationId, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new MarkConversationReadCommandHandler(db, CurrentUser(meUserId), notifier)
            .Handle(new MarkConversationReadCommand(conversationId), CancellationToken.None);
    }

    private async Task LeaveAsync(Guid meUserId, Guid conversationId, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new LeaveConversationCommandHandler(db, CurrentUser(meUserId), notifier)
            .Handle(new LeaveConversationCommand(conversationId), CancellationToken.None);
    }

    private async Task RemoveMemberAsync(Guid meUserId, Guid conversationId, Guid memberId, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new RemoveGroupMemberCommandHandler(db, CurrentUser(meUserId), notifier)
            .Handle(new RemoveGroupMemberCommand(conversationId, memberId), CancellationToken.None);
    }

    private async Task RenameAsync(Guid meUserId, Guid conversationId, string title, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new RenameGroupCommandHandler(db, CurrentUser(meUserId), notifier)
            .Handle(new RenameGroupCommand(conversationId, title), CancellationToken.None);
    }

    private async Task DeleteMessageAsync(Guid meUserId, Guid messageId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        await new DeleteMessageCommandHandler(db, CurrentUser(meUserId))
            .Handle(new DeleteMessageCommand(messageId), CancellationToken.None);
    }

    private static ICurrentUser CurrentUser(Guid userId)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(userId);
        return currentUser;
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
