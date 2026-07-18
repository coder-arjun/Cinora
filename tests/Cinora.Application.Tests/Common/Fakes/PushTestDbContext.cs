using System.Runtime.CompilerServices;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A real, in-process <see cref="IAppDbContext"/> backed by SQLite in-memory that maps the entities the
/// Milestone 6.2 Web-Push handlers touch — <see cref="User"/>, <see cref="Movie"/>, <see cref="Review"/>,
/// <see cref="ReviewLike"/>, <see cref="Notification"/>, and <see cref="Device"/> — so the device upsert, the
/// send handler's <c>NotificationProjection</c> reload, and the dispatch seam (driven through
/// <c>LikeReviewCommandHandler</c>) run against a genuine relational engine without a
/// <c>WebApplicationFactory</c>. Review→Movie is a query join (no navigation/FK), mirroring production. The
/// remaining sets are explicit-interface stubs that throw if touched, so EF never discovers them.
/// </summary>
internal sealed class PushTestDbContext(DbContextOptions<PushTestDbContext> options)
    : DbContext(options), IAppDbContext
{
    /// <summary>The profile/social users — mapped, for actor display names and the projection's actor sub-query.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>The cached titles — mapped, so the notification projection can join Review→Movie for the deep link.</summary>
    public DbSet<Movie> Movies => Set<Movie>();

    /// <summary>The reviews — mapped, so a review-type notification resolves its title coordinates.</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>The review likes — mapped, so the like handler (the dispatch seam) can insert and dedup.</summary>
    public DbSet<ReviewLike> ReviewLikes => Set<ReviewLike>();

    /// <summary>The notifications — mapped, so the send handler reloads and the seam recomputes the unread count.</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>The Web Push device subscriptions — mapped, for the upsert and the send/prune paths.</summary>
    public DbSet<Device> Devices => Set<Device>();

    DbSet<Friend> IAppDbContext.Friends => throw Unsupported();

    DbSet<UserBlock> IAppDbContext.UserBlocks => throw Unsupported();

    DbSet<Conversation> IAppDbContext.Conversations => throw Unsupported();

    DbSet<ConversationMember> IAppDbContext.ConversationMembers => throw Unsupported();

    DbSet<Message> IAppDbContext.Messages => throw Unsupported();

    DbSet<Genre> IAppDbContext.Genres => throw Unsupported();

    DbSet<MovieGenre> IAppDbContext.MovieGenres => throw Unsupported();

    DbSet<Comment> IAppDbContext.Comments => throw Unsupported();

    DbSet<MovieLike> IAppDbContext.MovieLikes => throw Unsupported();

    DbSet<Watchlist> IAppDbContext.Watchlists => throw Unsupported();

    DbSet<AIRecommendationHistory> IAppDbContext.AIRecommendationHistories => throw Unsupported();

    /// <inheritdoc />
    public void DiscardPendingChanges() => ChangeTracker.Clear();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<Friend>();
        modelBuilder.Ignore<UserBlock>();
        modelBuilder.Ignore<Conversation>();
        modelBuilder.Ignore<ConversationMember>();
        modelBuilder.Ignore<Message>();
        modelBuilder.Ignore<Genre>();
        modelBuilder.Ignore<MovieGenre>();
        modelBuilder.Ignore<Comment>();
        modelBuilder.Ignore<MovieLike>();
        modelBuilder.Ignore<Watchlist>();
        modelBuilder.Ignore<AIRecommendationHistory>();

        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            // Milestone 6.3: the owned NotificationPreferences (four columns on Users) must be mapped so the
            // send handler's per-type preference gate can project the recipient's flags.
            user.OwnsOne(u => u.Preferences);
        });

        modelBuilder.Entity<Movie>(movie =>
        {
            movie.HasKey(m => m.Id);
            movie.Property(m => m.Id).ValueGeneratedNever();
            movie.HasIndex(m => new { m.TmdbId, m.MediaType }).IsUnique();
        });

        // Review: bare Guid FKs (no navigations) — the projection joins Movies explicitly, like production.
        modelBuilder.Entity<Review>(review =>
        {
            review.HasKey(r => r.Id);
            review.Property(r => r.Id).ValueGeneratedNever();
            review.HasIndex(r => new { r.UserId, r.MovieId }).IsUnique();
            review.Property(r => r.Rating)
                  .HasConversion(r => r.Value, v => Rating.From(v))
                  .HasColumnName("Rating");
            review.Property(r => r.Body).HasMaxLength(Review.BodyMaxLength);
        });

        // ReviewLike: the composite (ReviewId, UserId) key — a user can like a review at most once.
        modelBuilder.Entity<ReviewLike>(like => like.HasKey(rl => new { rl.ReviewId, rl.UserId }));

        modelBuilder.Entity<Notification>(notification =>
        {
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Id).ValueGeneratedNever();
            notification.Property(n => n.Message).HasMaxLength(Notification.MessageMaxLength);
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.HasKey(d => d.Id);
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasIndex(d => d.UserId);
        });
    }

    private static NotSupportedException Unsupported([CallerMemberName] string? member = null) =>
        new($"{member} is not mapped by PushTestDbContext (only Users, Movies, Reviews, ReviewLikes, " +
            "Notifications and Devices are).");
}

/// <summary>
/// Hands out <see cref="PushTestDbContext"/> instances sharing ONE isolated SQLite in-memory database (a single
/// open connection), so a handler can persist and a sibling context can verify the committed rows. A fresh
/// instance per test keeps stores isolated.
/// </summary>
internal sealed class PushSqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Opens the shared in-memory connection and creates the schema once.</summary>
    public PushSqliteStore()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    /// <summary>Creates a new context over this store's single shared connection.</summary>
    public PushTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PushTestDbContext>()
            .UseSqlite(_connection)
            .Options);

    /// <summary>Closes the shared connection, dropping the in-memory database.</summary>
    public void Dispose() => _connection.Dispose();
}
