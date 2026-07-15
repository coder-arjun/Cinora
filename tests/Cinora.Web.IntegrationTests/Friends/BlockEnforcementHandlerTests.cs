using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Realtime;
using Cinora.Application.Features.Friends;
using Cinora.Application.Features.Profiles;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Task B1.4 handler-level tests for block enforcement in <see cref="GetProfileQueryHandler"/> and
/// <see cref="SendFriendRequestCommandHandler"/>. These do NOT depend on the HTTP block endpoints (those arrive
/// in Task B1.5) — a <see cref="UserBlock"/> row is seeded directly via a scoped <see cref="CinoraDbContext"/>
/// and each handler is constructed directly with an NSubstitute <see cref="ICurrentUser"/> standing in for the
/// acting/viewing user, mirroring the pattern in <c>BlockingTests</c> (Task B1.3).
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class BlockEnforcementHandlerTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public BlockEnforcementHandlerTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetProfile_throws_not_found_when_the_owner_has_blocked_the_viewer()
    {
        using var owner = await TestAuthentication.RegisterAndSignInAsync(_factory, "Owner Blocks Viewer");
        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Viewer");
        await SeedBlockAsync(blockerId: owner.UserId, blockedUserId: viewer.UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(viewer.UserId);
        var handler = new GetProfileQueryHandler(db, currentUser);

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.Handle(new GetProfileQuery(owner.UserId), CancellationToken.None));
    }

    [Fact]
    public async Task GetProfile_returns_BlockedByMe_with_no_content_when_the_viewer_has_blocked_the_owner()
    {
        using var owner = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Owner");
        using var viewer = await TestAuthentication.RegisterAndSignInAsync(_factory, "Viewer Blocks Owner");
        await SeedBlockAsync(blockerId: viewer.UserId, blockedUserId: owner.UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(viewer.UserId);
        var handler = new GetProfileQueryHandler(db, currentUser);

        var vm = await handler.Handle(new GetProfileQuery(owner.UserId), CancellationToken.None);

        Assert.Equal(ProfileRelationship.BlockedByMe, vm.Relationship);
        Assert.Empty(vm.Reviews);
        Assert.Empty(vm.Friends);
        Assert.Empty(vm.WatchlistHighlights);
        Assert.False(vm.CanViewReviews);
        Assert.False(vm.CanViewWatchlist);
        Assert.False(vm.CanViewFriends);
        Assert.Equal(0, vm.FriendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendFriendRequest_returns_Blocked_and_creates_no_row_when_a_block_exists_either_direction(
        bool senderBlockedRecipient)
    {
        using var sender = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Sender Or Recipient A");
        using var recipient = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Sender Or Recipient B");

        if (senderBlockedRecipient)
        {
            await SeedBlockAsync(blockerId: sender.UserId, blockedUserId: recipient.UserId);
        }
        else
        {
            await SeedBlockAsync(blockerId: recipient.UserId, blockedUserId: sender.UserId);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(sender.UserId);
        var handler = new SendFriendRequestCommandHandler(
            db,
            currentUser,
            Substitute.For<IRealtimeNotifier>(),
            Substitute.For<IPushDispatch>(),
            NullLogger<SendFriendRequestCommandHandler>.Instance);

        var result = await handler.Handle(
            new SendFriendRequestCommand(recipient.UserId), CancellationToken.None);

        Assert.Equal(FriendRequestOutcome.Blocked, result.Outcome);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.False(await verifyDb.Friends.AnyAsync(
            friend => (friend.RequesterId == sender.UserId && friend.AddresseeId == recipient.UserId)
                || (friend.RequesterId == recipient.UserId && friend.AddresseeId == sender.UserId)));
    }

    private async Task SeedBlockAsync(Guid blockerId, Guid blockedUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        db.UserBlocks.Add(UserBlock.Create(blockerId, blockedUserId));
        await db.SaveChangesAsync();
    }
}
