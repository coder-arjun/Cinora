using Cinora.Application.Features.Profiles;
using Cinora.Application.Tests.Common.Fakes;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Application.Tests.Features.Profiles;

/// <summary>
/// Deterministic unit tests for the Milestone 4.2 avatar handlers' ADR 0013 §2.5 ordering (save → set key →
/// best-effort cleanup), against an in-process SQLite <see cref="ProfilesTestDbContext"/> and a
/// <see cref="RecordingFileStorage"/>. They prove the two hard cases the shared-database integration suite
/// cannot force deterministically: the step-2-fails-after-step-1 rollback (the just-saved file is deleted and the
/// DB is left unchanged) and the replace/remove cleanup ordering. The happy-path replace is also covered
/// end-to-end in the integration suite (P4).
/// </summary>
public sealed class AvatarCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string OldKey = "avatars/11111111111111111111111111111111.jpg";
    private const string NewKey = "avatars/22222222222222222222222222222222.png";

    [Fact]
    public async Task Change_avatar_persists_the_new_key_and_deletes_only_the_old_file()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: OldKey);

        var storage = new RecordingFileStorage { SaveReturns = NewKey };
        await using var db = store.CreateContext();
        var handler = new ChangeAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<ChangeAvatarCommandHandler>.Instance);

        var result = await handler.Handle(
            new ChangeAvatarCommand(new MemoryStream([1, 2, 3]), "image/png", 3), CancellationToken.None);

        Assert.Equal(NewKey, result.AvatarFileKey);

        // The committed row points at the new key.
        Assert.Equal(NewKey, await AvatarKeyAsync(store));

        // Only the replaced (old) file was deleted — the just-saved new file is kept.
        Assert.Equal([OldKey], storage.DeletedKeys);
    }

    [Fact]
    public async Task Change_avatar_from_no_avatar_deletes_nothing()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: null);

        var storage = new RecordingFileStorage { SaveReturns = NewKey };
        await using var db = store.CreateContext();
        var handler = new ChangeAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<ChangeAvatarCommandHandler>.Instance);

        var result = await handler.Handle(
            new ChangeAvatarCommand(new MemoryStream([1, 2, 3]), "image/png", 3), CancellationToken.None);

        Assert.Equal(NewKey, result.AvatarFileKey);
        Assert.Equal(NewKey, await AvatarKeyAsync(store));
        Assert.Empty(storage.DeletedKeys); // there was no prior file to clean up
    }

    [Fact]
    public async Task Change_avatar_deletes_the_new_file_and_leaves_the_db_unchanged_when_the_save_fails()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: OldKey);

        var storage = new RecordingFileStorage { SaveReturns = NewKey };
        await using var db = store.CreateContext();
        db.ThrowOnNextSave = true; // step 2 (the DB commit) fails after step 1 saved the bytes
        var handler = new ChangeAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<ChangeAvatarCommandHandler>.Instance);

        await Assert.ThrowsAsync<DbUpdateException>(() => handler.Handle(
            new ChangeAvatarCommand(new MemoryStream([1, 2, 3]), "image/png", 3), CancellationToken.None));

        // The just-saved orphan is best-effort deleted; the old file is NOT (step 3 never ran).
        Assert.Equal([NewKey], storage.DeletedKeys);

        // The DB is unchanged — the user still points at the old key (the failed save committed nothing).
        Assert.Equal(OldKey, await AvatarKeyAsync(store));
    }

    [Fact]
    public async Task Change_avatar_swallows_a_best_effort_cleanup_failure_and_still_succeeds()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: OldKey);

        // The replace succeeds, but deleting the old file fails — the request must still succeed (200) and the
        // new key must remain committed (a delete failure only leaves an orphan; it never fails the request).
        var storage = new RecordingFileStorage
        {
            SaveReturns = NewKey,
            DeleteThrows = new IOException("disk gremlin"),
        };
        await using var db = store.CreateContext();
        var handler = new ChangeAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<ChangeAvatarCommandHandler>.Instance);

        var result = await handler.Handle(
            new ChangeAvatarCommand(new MemoryStream([1, 2, 3]), "image/png", 3), CancellationToken.None);

        Assert.Equal(NewKey, result.AvatarFileKey);
        Assert.Equal(NewKey, await AvatarKeyAsync(store));
        Assert.Equal([OldKey], storage.DeletedKeys); // it TRIED to delete the old file
    }

    [Fact]
    public async Task Remove_avatar_clears_the_key_and_deletes_the_file()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: OldKey);

        var storage = new RecordingFileStorage();
        await using var db = store.CreateContext();
        var handler = new RemoveAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<RemoveAvatarCommandHandler>.Instance);

        await handler.Handle(new RemoveAvatarCommand(), CancellationToken.None);

        Assert.Null(await AvatarKeyAsync(store));
        Assert.Equal([OldKey], storage.DeletedKeys);
    }

    [Fact]
    public async Task Remove_avatar_is_a_noop_when_there_is_no_avatar()
    {
        using var store = new ProfilesSqliteStore();
        await SeedUserAsync(store, avatarKey: null);

        var storage = new RecordingFileStorage();
        await using var db = store.CreateContext();
        db.ThrowOnNextSave = true; // a no-op must NOT save; if it did, this would throw
        var handler = new RemoveAvatarCommandHandler(
            storage, db, new StubCurrentUser(UserId), NullLogger<RemoveAvatarCommandHandler>.Instance);

        // No throw (no save happened), no delete, key stays null.
        await handler.Handle(new RemoveAvatarCommand(), CancellationToken.None);

        Assert.Null(await AvatarKeyAsync(store));
        Assert.Empty(storage.DeletedKeys);
    }

    private static async Task SeedUserAsync(ProfilesSqliteStore store, string? avatarKey)
    {
        await using var db = store.CreateContext();
        var user = User.Create(UserId, "Ada Owner");
        if (avatarKey is not null)
        {
            user.SetAvatar(avatarKey);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static async Task<string?> AvatarKeyAsync(ProfilesSqliteStore store)
    {
        await using var db = store.CreateContext();
        return await db.Users.AsNoTracking()
            .Where(user => user.Id == UserId)
            .Select(user => user.AvatarFileKey)
            .SingleAsync();
    }
}
