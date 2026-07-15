using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserBlockPersistenceTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserBlockPersistenceTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UserBlock_round_trips_and_duplicate_pair_violates_the_unique_index()
    {
        // UserBlockConfiguration enforces a real FK (Restrict) from BlockerId/BlockedUserId to Users, mirroring
        // FriendConfiguration — so, like FriendProfileTests, the pair must be REAL registered users (the shared
        // ApplicationUser/User primary key) rather than arbitrary GUIDs; a raw GUID would fail with a
        // FOREIGN KEY violation before ever reaching the unique-index assertion this test is about.
        using var blockerUser = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocker UB1");
        using var blockedUser = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked UB1");
        var blocker = blockerUser.UserId;
        var blocked = blockedUser.UserId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            db.UserBlocks.Add(UserBlock.Create(blocker, blocked));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            Assert.True(await db.UserBlocks.AsNoTracking()
                .AnyAsync(b => b.BlockerId == blocker && b.BlockedUserId == blocked));

            db.UserBlocks.Add(UserBlock.Create(blocker, blocked)); // same directed pair again
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
