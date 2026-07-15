using System.Runtime.CompilerServices;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A real, in-process <see cref="IAppDbContext"/> backed by SQLite in-memory that maps ONLY the
/// <see cref="User"/> aggregate — enough to unit-test the Milestone 4.2 profile/avatar handlers' ADR 0013 §2.5
/// ordering (save → set key → best-effort cleanup) without a <c>WebApplicationFactory</c>. The other eleven
/// <see cref="IAppDbContext"/> sets are explicit-interface stubs that throw if touched, so EF never discovers
/// them into the model. <see cref="ThrowOnNextSave"/> simulates a failed commit (the step-2-fails-after-step-1
/// path) so the test can assert the just-saved file is best-effort deleted and the DB is left unchanged.
/// </summary>
internal sealed class ProfilesTestDbContext(DbContextOptions<ProfilesTestDbContext> options)
    : DbContext(options), IAppDbContext
{
    /// <summary>When <see langword="true"/>, the next <see cref="SaveChangesAsync(CancellationToken)"/> throws a
    /// <see cref="DbUpdateException"/> before writing anything (one-shot) — the failed-commit simulation.</summary>
    public bool ThrowOnNextSave { get; set; }

    /// <summary>The profile/social user aggregates — the only mapped set.</summary>
    public DbSet<User> Users => Set<User>();

    DbSet<Friend> IAppDbContext.Friends => throw Unsupported();

    DbSet<UserBlock> IAppDbContext.UserBlocks => throw Unsupported();

    DbSet<Conversation> IAppDbContext.Conversations => throw Unsupported();

    DbSet<ConversationMember> IAppDbContext.ConversationMembers => throw Unsupported();

    DbSet<Message> IAppDbContext.Messages => throw Unsupported();

    DbSet<Movie> IAppDbContext.Movies => throw Unsupported();

    DbSet<Genre> IAppDbContext.Genres => throw Unsupported();

    DbSet<MovieGenre> IAppDbContext.MovieGenres => throw Unsupported();

    DbSet<Review> IAppDbContext.Reviews => throw Unsupported();

    DbSet<ReviewLike> IAppDbContext.ReviewLikes => throw Unsupported();

    DbSet<Comment> IAppDbContext.Comments => throw Unsupported();

    DbSet<Watchlist> IAppDbContext.Watchlists => throw Unsupported();

    DbSet<Notification> IAppDbContext.Notifications => throw Unsupported();

    DbSet<Device> IAppDbContext.Devices => throw Unsupported();

    DbSet<AIRecommendationHistory> IAppDbContext.AIRecommendationHistories => throw Unsupported();

    /// <inheritdoc />
    public void DiscardPendingChanges() => ChangeTracker.Clear();

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextSave)
        {
            ThrowOnNextSave = false; // one-shot
            throw new DbUpdateException("Simulated commit failure.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<Friend>();
        modelBuilder.Ignore<UserBlock>();
        modelBuilder.Ignore<Conversation>();
        modelBuilder.Ignore<ConversationMember>();
        modelBuilder.Ignore<Message>();
        modelBuilder.Ignore<Movie>();
        modelBuilder.Ignore<Genre>();
        modelBuilder.Ignore<MovieGenre>();
        modelBuilder.Ignore<Review>();
        modelBuilder.Ignore<ReviewLike>();
        modelBuilder.Ignore<Comment>();
        modelBuilder.Ignore<Watchlist>();
        modelBuilder.Ignore<Notification>();
        modelBuilder.Ignore<Device>();
        modelBuilder.Ignore<AIRecommendationHistory>();

        // Surrogate Guid key set by the domain factory (client-set), matching the production configuration.
        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            // Milestone 6.3: map the owned NotificationPreferences (four columns on Users) so the
            // notification-preference command/query handlers run against a genuine relational schema.
            user.OwnsOne(u => u.Preferences);
        });
    }

    private static NotSupportedException Unsupported([CallerMemberName] string? member = null) =>
        new($"{member} is not mapped by ProfilesTestDbContext (only Users is).");
}

/// <summary>
/// Hands out <see cref="ProfilesTestDbContext"/> instances sharing ONE isolated SQLite in-memory database (a
/// single open connection), so a context can seed a user and a sibling context can read the committed row after
/// a simulated failed save — proving the DB was left unchanged. A fresh instance per test keeps stores isolated.
/// </summary>
internal sealed class ProfilesSqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens the shared in-memory connection and creates the <see cref="User"/> schema once.</summary>
    public ProfilesSqliteStore()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    /// <summary>Creates a new context over this store's single shared connection.</summary>
    public ProfilesTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ProfilesTestDbContext>()
            .UseSqlite(_connection)
            .Options);

    /// <summary>Closes the shared connection, dropping the in-memory database.</summary>
    public void Dispose() => _connection.Dispose();
}
