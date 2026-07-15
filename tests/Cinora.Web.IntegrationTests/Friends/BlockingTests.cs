using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Blocks;
using Cinora.Domain.Entities;
using Cinora.Domain.Exceptions;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Task B1.3 tests for the block/unblock/list Application verticals (<see cref="BlockUserCommand"/>,
/// <see cref="UnblockUserCommand"/>, <see cref="GetBlockedUsersQuery"/>). Users are seeded through
/// <see cref="TestAuthentication.RegisterAndSignInAsync"/> so the real Identity + domain <c>User</c> rows exist
/// (the <c>UserBlock</c>/<c>Friend</c> FKs reject arbitrary GUIDs — see <c>UserBlockPersistenceTests</c>).
/// Each command/query is driven by constructing its handler directly against a scoped
/// <see cref="CinoraDbContext"/> with an NSubstitute <see cref="ICurrentUser"/> standing in for the acting user,
/// rather than through <c>ISender</c> — outside an HTTP request there is no populated
/// <c>IHttpContextAccessor</c> for the real <c>ICurrentUser</c> implementation to read. The HTTP-level wiring
/// (controller endpoints) is covered separately in Task B1.5.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class BlockingTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public BlockingTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Block_removes_existing_friendship_and_is_silent_and_idempotent()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me B1");
        using var other = await TestAuthentication.RegisterAndSignInAsync(_factory, "Other B1");
        await SeedAcceptedFriendshipAsync(me.UserId, other.UserId);

        await BlockAsync(me.UserId, other.UserId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.True(await db.UserBlocks.AnyAsync(b => b.BlockerId == me.UserId && b.BlockedUserId == other.UserId));
        Assert.False(await db.Friends.AnyAsync(f =>
            (f.RequesterId == me.UserId && f.AddresseeId == other.UserId)
            || (f.RequesterId == other.UserId && f.AddresseeId == me.UserId)));
        Assert.False(await db.Notifications.AnyAsync(n => n.RecipientUserId == other.UserId)); // silent

        // Idempotent: a second block is a no-op, not a duplicate/throw.
        await BlockAsync(me.UserId, other.UserId);
        Assert.Equal(1, await db.UserBlocks.CountAsync(b => b.BlockerId == me.UserId && b.BlockedUserId == other.UserId));
    }

    [Fact]
    public async Task Block_self_throws_domain_exception()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Solo B1");

        await Assert.ThrowsAsync<DomainException>(() => BlockAsync(me.UserId, me.UserId));
    }

    [Fact]
    public async Task Unblock_removes_the_block_and_is_idempotent()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me B1b");
        using var other = await TestAuthentication.RegisterAndSignInAsync(_factory, "Other B1b");
        await BlockAsync(me.UserId, other.UserId);

        await UnblockAsync(me.UserId, other.UserId);
        await UnblockAsync(me.UserId, other.UserId); // idempotent no-op

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.False(await db.UserBlocks.AnyAsync(b => b.BlockerId == me.UserId && b.BlockedUserId == other.UserId));
    }

    [Fact]
    public async Task Blocked_users_query_returns_only_who_i_blocked()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Me B1c");
        using var other = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Person");
        await BlockAsync(me.UserId, other.UserId);

        var vm = await GetBlockedUsersAsync(me.UserId);

        Assert.Single(vm.Blocked);
        Assert.Equal(other.UserId, vm.Blocked[0].UserId);
        Assert.Equal(other.DisplayName, vm.Blocked[0].DisplayName);
    }

    // ---- handler-driven helpers (constructs handlers directly; no ISender / HTTP context needed) ----

    private async Task BlockAsync(Guid meUserId, Guid targetUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        var handler = new BlockUserCommandHandler(db, currentUser);
        await handler.Handle(new BlockUserCommand(targetUserId), CancellationToken.None);
    }

    private async Task UnblockAsync(Guid meUserId, Guid targetUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        var handler = new UnblockUserCommandHandler(db, currentUser);
        await handler.Handle(new UnblockUserCommand(targetUserId), CancellationToken.None);
    }

    private async Task<BlockedUsersVm> GetBlockedUsersAsync(Guid meUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        var handler = new GetBlockedUsersQueryHandler(db, currentUser);
        return await handler.Handle(new GetBlockedUsersQuery(), CancellationToken.None);
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
