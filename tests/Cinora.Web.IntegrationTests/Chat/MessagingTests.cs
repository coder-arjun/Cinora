using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Chat;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C2.3 tests for <see cref="SendMessageCommand"/> and <see cref="GetMessagesQuery"/> — membership gate,
/// DM block re-check, the best-effort <see cref="IChatNotifier"/> push, keyset paging, and the raw-body
/// (encoding-is-the-view's-job) contract. Handlers are driven by direct construction with an NSubstitute
/// <see cref="ICurrentUser"/> and <see cref="IChatNotifier"/> (mirrors <c>BlockingTests</c>); users are seeded
/// through registration so the chat FKs to <c>Users</c> are satisfied.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class MessagingTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public MessagingTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_member_send_persists_the_message_bumps_the_conversation_and_pushes()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23a");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23a");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        var notifier = Substitute.For<IChatNotifier>();
        var vm = await SendAsync(me.UserId, conversationId, "Hey there", notifier);

        Assert.True(vm.IsMine);
        Assert.Equal("Hey there", vm.Body);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.True(await db.Messages.AsNoTracking()
            .AnyAsync(m => m.Id == vm.Id && m.SenderId == me.UserId && m.Body == "Hey there"));
        var lastMessageAt = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId).Select(c => c.LastMessageAtUtc).FirstAsync();
        Assert.Equal(vm.SentAtUtc, lastMessageAt);

        await notifier.Received(1).MessageSentAsync(
            conversationId,
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Is<ChatMessageDto>(d => d.Id == vm.Id && d.Body == "Hey there"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_member_send_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23b");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23b");
        using var outsider = await TestAuthentication.RegisterAndSignInAsync(_factory, "Outsider C23b");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            SendAsync(outsider.UserId, conversationId, "Let me in", Substitute.For<IChatNotifier>()));
    }

    [Fact]
    public async Task Sending_to_a_dm_after_a_block_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23c");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23c");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        await SeedBlockAsync(friend.UserId, me.UserId); // the other party blocks me after the DM exists

        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            SendAsync(me.UserId, conversationId, "Still there?", Substitute.For<IChatNotifier>()));
    }

    [Fact]
    public async Task Get_messages_pages_newest_first_with_no_overlap()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23d");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23d");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        var notifier = Substitute.For<IChatNotifier>();
        for (var i = 1; i <= 5; i++)
        {
            await SendAsync(me.UserId, conversationId, $"msg {i}", notifier);
        }

        var page1 = await GetMessagesAsync(me.UserId, conversationId, cursorSentAt: null, cursorId: null, take: 2);
        Assert.Equal(2, page1.Messages.Count);
        Assert.True(page1.HasMore);
        Assert.True(page1.Messages[0].SentAtUtc >= page1.Messages[1].SentAtUtc); // internally newest-first

        var page2 = await GetMessagesAsync(
            me.UserId, conversationId, page1.NextCursorSentAt, page1.NextCursorId, take: 2);
        Assert.Equal(2, page2.Messages.Count);
        Assert.True(page2.HasMore);

        var page1Ids = page1.Messages.Select(m => m.Id).ToHashSet();
        var page2Ids = page2.Messages.Select(m => m.Id).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids)); // no overlap across the boundary
        // Every page-1 message is at least as new as every page-2 message (newest-first across pages).
        Assert.True(page1.Messages.Min(m => m.SentAtUtc) >= page2.Messages.Max(m => m.SentAtUtc));
    }

    [Fact]
    public async Task A_script_body_is_persisted_and_returned_verbatim()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23e");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23e");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        const string payload = "<script>alert('x')</script>";
        var vm = await SendAsync(me.UserId, conversationId, payload, Substitute.For<IChatNotifier>());

        Assert.Equal(payload, vm.Body); // encoding is the view's job; the VM carries the raw text

        var thread = await GetMessagesAsync(me.UserId, conversationId, null, null, take: 10);
        Assert.Contains(thread.Messages, m => m.Body == payload);
    }

    [Fact]
    public async Task Sharing_a_movie_persists_the_card_and_returns_it_in_history()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23f");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23f");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        var notifier = Substitute.For<IChatNotifier>();
        var vm = await ShareMovieAsync(me.UserId, conversationId, 550, "Movie", "Fight Club", "/poster.jpg", "must watch!", notifier);

        Assert.Equal(550, vm.SharedMovieTmdbId);
        Assert.Equal("Fight Club", vm.SharedMovieTitle);
        Assert.Equal("must watch!", vm.Body); // caption

        var thread = await GetMessagesAsync(me.UserId, conversationId, null, null, take: 10);
        Assert.Contains(thread.Messages, m => m.SharedMovieTmdbId == 550 && m.SharedMovieTitle == "Fight Club");

        await notifier.Received(1).MessageSentAsync(
            conversationId,
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Is<ChatMessageDto>(d => d.SharedMovieTmdbId == 550 && d.SharedMovieTitle == "Fight Club"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sharing_a_movie_into_a_conversation_you_are_not_in_is_forbidden()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me C23g");
        using var friend = await TestAuthentication.RegisterAndSignInAsync(_factory, "Friend C23g");
        using var outsider = await TestAuthentication.RegisterAndSignInAsync(_factory, "Outsider C23g");
        await SeedAcceptedFriendshipAsync(me.UserId, friend.UserId);
        var conversationId = await StartDirectAsync(me.UserId, friend.UserId);

        await Assert.ThrowsAsync<ForbiddenAccessException>(() =>
            ShareMovieAsync(outsider.UserId, conversationId, 550, "Movie", "Fight Club", null, null, Substitute.For<IChatNotifier>()));
    }

    // ---- handler-driven helpers ----

    private async Task<ChatMessageVm> ShareMovieAsync(
        Guid meUserId, Guid conversationId, int tmdbId, string mediaType, string title, string? posterPath, string? caption, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        return await new ShareMovieToConversationCommandHandler(db, currentUser, notifier)
            .Handle(new ShareMovieToConversationCommand(conversationId, tmdbId, mediaType, title, posterPath, caption), CancellationToken.None);
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

    private async Task<ChatMessageVm> SendAsync(Guid meUserId, Guid conversationId, string body, IChatNotifier notifier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        return await new SendMessageCommandHandler(db, currentUser, notifier)
            .Handle(new SendMessageCommand(conversationId, body), CancellationToken.None);
    }

    private async Task<MessageThreadVm> GetMessagesAsync(
        Guid meUserId, Guid conversationId, DateTime? cursorSentAt, Guid? cursorId, int take)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        return await new GetMessagesQueryHandler(db, currentUser)
            .Handle(new GetMessagesQuery(conversationId, cursorSentAt, cursorId, take), CancellationToken.None);
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
