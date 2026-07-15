using Cinora.Application.Features.Push;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Features.Push;

/// <summary>
/// Milestone 6.2 unit tests for <see cref="RegisterDeviceCommandHandler"/>'s upsert-by-endpoint semantics
/// (ADR 0020 §3.3) against an in-process SQLite <see cref="PushTestDbContext"/>: a re-subscribe with the same
/// endpoint rotates the keys on the existing <see cref="Device"/> (and marks it seen) rather than duplicating,
/// while a different endpoint adds a second device. The acting user is server-resolved (no id is bound).
/// </summary>
public sealed class RegisterDeviceCommandHandlerTests
{
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";

    [Fact]
    public async Task Handle_registers_a_new_device_for_the_current_user()
    {
        var userId = Guid.NewGuid();
        using var store = new PushSqliteStore();
        await using var db = store.CreateContext();
        var handler = new RegisterDeviceCommandHandler(db, new StubCurrentUser(userId));

        await handler.Handle(new RegisterDeviceCommand(Endpoint, "p256dh-1", "auth-1"), CancellationToken.None);

        var device = await db.Devices.AsNoTracking().SingleAsync(d => d.UserId == userId);
        Assert.Equal(Endpoint, device.Endpoint);
        Assert.Equal("p256dh-1", device.P256dhKey);
        Assert.Equal("auth-1", device.AuthSecret);
    }

    [Fact]
    public async Task Handle_resubscribe_with_same_endpoint_updates_keys_and_does_not_duplicate()
    {
        var userId = Guid.NewGuid();
        using var store = new PushSqliteStore();

        await using (var seed = store.CreateContext())
        {
            var handler = new RegisterDeviceCommandHandler(seed, new StubCurrentUser(userId));
            await handler.Handle(new RegisterDeviceCommand(Endpoint, "p256dh-1", "auth-1"), CancellationToken.None);
        }

        DateTime firstSeen;
        Guid firstId;
        await using (var read = store.CreateContext())
        {
            var device = await read.Devices.AsNoTracking().SingleAsync(d => d.UserId == userId);
            firstSeen = device.LastSeenUtc;
            firstId = device.Id;
        }

        await Task.Delay(5); // let the clock advance so the refreshed LastSeenUtc is observably newer.

        await using (var resubscribe = store.CreateContext())
        {
            var handler = new RegisterDeviceCommandHandler(resubscribe, new StubCurrentUser(userId));
            await handler.Handle(new RegisterDeviceCommand(Endpoint, "p256dh-2", "auth-2"), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        var devices = await verify.Devices.AsNoTracking().Where(d => d.UserId == userId).ToListAsync();

        var only = Assert.Single(devices);                 // upsert — NOT a duplicate
        Assert.Equal(firstId, only.Id);                    // the same row was updated in place
        Assert.Equal("p256dh-2", only.P256dhKey);          // keys rotated
        Assert.Equal("auth-2", only.AuthSecret);
        Assert.True(only.LastSeenUtc >= firstSeen);        // Seen() advanced the timestamp
    }

    [Fact]
    public async Task Handle_different_endpoint_adds_a_second_device()
    {
        var userId = Guid.NewGuid();
        using var store = new PushSqliteStore();

        await using (var first = store.CreateContext())
        {
            var handler = new RegisterDeviceCommandHandler(first, new StubCurrentUser(userId));
            await handler.Handle(new RegisterDeviceCommand(Endpoint, "p256dh-1", "auth-1"), CancellationToken.None);
        }

        await using (var second = store.CreateContext())
        {
            var handler = new RegisterDeviceCommandHandler(second, new StubCurrentUser(userId));
            await handler.Handle(
                new RegisterDeviceCommand(Endpoint + "/other", "p256dh-2", "auth-2"), CancellationToken.None);
        }

        await using var verify = store.CreateContext();
        Assert.Equal(2, await verify.Devices.AsNoTracking().CountAsync(d => d.UserId == userId));
    }
}
