using System.Runtime.CompilerServices;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Tests.Common.Fakes;

/// <summary>
/// A real, in-process <see cref="IAppDbContext"/> backed by SQLite in-memory (a single shared open
/// connection handed out by <see cref="SharedSqliteStore"/>). Unlike the EF Core InMemory provider it was
/// migrated from, SQLite is a genuine relational engine that ENFORCES the unique indexes
/// (<see cref="Genre.TmdbGenreId"/>, <see cref="Movie"/>'s <c>(TmdbId, MediaType)</c>), so a duplicate
/// insert throws a real <see cref="DbUpdateException"/> — the only way to reproduce the genre-insert-race
/// 500 (H-1); InMemory silently ignored the indexes and could not. Only the three catalog entities the race
/// handlers touch are mapped (<see cref="Movie"/>, <see cref="Genre"/>, <see cref="MovieGenre"/>); the rest
/// are explicit-interface stubs that throw if used, so EF never discovers them into the model.
///
/// The key affordance is <see cref="RaceOnNextSave"/>: set it and the very next <c>SaveChangesAsync</c>
/// first runs the supplied action (typically a sibling context committing the conflicting "raced" row into
/// the shared store) and THEN delegates to the real SQLite save — which rejects our now-duplicate insert
/// with a genuine <see cref="DbUpdateException"/>, exactly as a concurrent writer tripping a unique index
/// would. No synthetic throw: the engine enforces the index. Because that save's transaction rolls back,
/// our own <c>Add</c>ed rows are never committed, so the subsequent no-tracking reload sees only the raced
/// row — the miss-then-hit the handlers reconcile against.
/// </summary>
internal sealed class TestAppDbContext(DbContextOptions<TestAppDbContext> options)
    : DbContext(options), IAppDbContext
{
    /// <summary>
    /// When set, the next <see cref="SaveChangesAsync(CancellationToken)"/> invokes this action (which
    /// commits the conflicting row via a sibling context on the shared connection) and then proceeds with
    /// the real save, so SQLite's unique index rejects our insert. One-shot: it clears itself so only the
    /// first save is raced.
    /// </summary>
    public Action? RaceOnNextSave { get; set; }

    /// <summary>The cached movie titles — mapped, so the handlers can query and stage against it.</summary>
    public DbSet<Movie> Movies => Set<Movie>();

    /// <summary>The TMDB genres — mapped, so the sync handler can query and stage against it.</summary>
    public DbSet<Genre> Genres => Set<Genre>();

    /// <summary>The movie/genre links — mapped, so the title write can persist and assert its links.</summary>
    public DbSet<MovieGenre> MovieGenres => Set<MovieGenre>();

    /// <summary>The user watchlist entries — mapped, so the watchlist handlers can query/upsert against them.</summary>
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();

    /// <summary>The user reviews — mapped (Milestone 5.1), so the taste-profile builder can query them.</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>The AI recommendation audit history — mapped (Milestone 5.2), so the generate command can persist
    /// a row and the serve query can read the latest one.</summary>
    public DbSet<AIRecommendationHistory> AIRecommendationHistories => Set<AIRecommendationHistory>();

    // The remaining IAppDbContext sets are not exercised by the catalog / watchlist / recommendation handlers.
    // They are explicit interface implementations (not public DbSet<T> properties) so EF never discovers them
    // into the model, keeping the SQLite schema to just Movie, Genre, MovieGenre, Watchlist and Review; touching
    // one is a test wiring bug.
    DbSet<User> IAppDbContext.Users => throw Unsupported();

    DbSet<Friend> IAppDbContext.Friends => throw Unsupported();

    DbSet<UserBlock> IAppDbContext.UserBlocks => throw Unsupported();

    DbSet<Conversation> IAppDbContext.Conversations => throw Unsupported();

    DbSet<ConversationMember> IAppDbContext.ConversationMembers => throw Unsupported();

    DbSet<Message> IAppDbContext.Messages => throw Unsupported();

    DbSet<ReviewLike> IAppDbContext.ReviewLikes => throw Unsupported();

    DbSet<Comment> IAppDbContext.Comments => throw Unsupported();

    DbSet<Notification> IAppDbContext.Notifications => throw Unsupported();

    DbSet<Device> IAppDbContext.Devices => throw Unsupported();

    /// <inheritdoc />
    // Mirrors CinoraDbContext: ChangeTracker.Clear() detaches every tracked entity, discarding all pending
    // inserts/updates/deletes so the next SaveChanges writes none of them. This is the exact seam the H-1
    // regression exercises — without it the handler's leftover Added genre re-flushes into step 2.
    public void DiscardPendingChanges() => ChangeTracker.Clear();

    /// <summary>
    /// Maps only the three catalog entities the race handlers touch, with the SAME keys and unique indexes
    /// the production configurations define — SQLite enforces those indexes, which is the point. The other
    /// nine entity types are ignored so EF does not try to build the full schema from the stub DbSets.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<User>();
        modelBuilder.Ignore<Friend>();
        modelBuilder.Ignore<UserBlock>();
        modelBuilder.Ignore<Conversation>();
        modelBuilder.Ignore<ConversationMember>();
        modelBuilder.Ignore<Message>();
        modelBuilder.Ignore<ReviewLike>();
        modelBuilder.Ignore<Comment>();
        modelBuilder.Ignore<Notification>();
        modelBuilder.Ignore<Device>();

        // Movie: surrogate Guid key (client-set) + the real unique (TmdbId, MediaType) index, so a duplicate
        // title insert throws a genuine DbUpdateException — the F5 title race.
        modelBuilder.Entity<Movie>(movie =>
        {
            movie.HasKey(m => m.Id);
            movie.Property(m => m.Id).ValueGeneratedNever();
            movie.HasIndex(m => new { m.TmdbId, m.MediaType }).IsUnique();
        });

        // Genre: surrogate Guid key (client-set) + the real unique TmdbGenreId index, so a duplicate genre
        // insert throws a genuine DbUpdateException — the F1 / H-1 genre race.
        modelBuilder.Entity<Genre>(genre =>
        {
            genre.HasKey(g => g.Id);
            genre.Property(g => g.Id).ValueGeneratedNever();
            genre.HasIndex(g => g.TmdbGenreId).IsUnique();
        });

        // MovieGenre: the composite-key join, so step 2's title write can persist its genre links and the
        // H-1 regression can assert them.
        modelBuilder.Entity<MovieGenre>(link => link.HasKey(mg => new { mg.MovieId, mg.GenreId }));

        // Watchlist: surrogate Guid key (client-set) + the real unique (UserId, MovieId) index, so the upsert
        // handler's race path throws a genuine DbUpdateException and the batch map joins against Movie by id.
        modelBuilder.Entity<Watchlist>(watchlist =>
        {
            watchlist.HasKey(w => w.Id);
            watchlist.Property(w => w.Id).ValueGeneratedNever();
            watchlist.HasIndex(w => new { w.UserId, w.MovieId }).IsUnique();
        });

        // Review: surrogate Guid key (client-set) + the SAME Rating value-object conversion the production
        // ReviewConfiguration uses (stored as its underlying int), so the taste-profile builder's ordering and
        // projection over Rating behave exactly as in production. No User/Movie FK is configured here because
        // User is ignored (and the builder joins Movies explicitly, not via a navigation) — a bare Guid FK
        // column matches how the builder queries.
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

        // AIRecommendationHistory (Milestone 5.2): the SAME key, string caps, and (UserId, GeneratedAtUtc) index
        // the production AIRecommendationHistoryConfiguration defines, so the generate command's persistence and
        // the serve query's "latest row" projection behave exactly as against SQL Server. No User FK is configured
        // (User is ignored; the row carries a bare Guid UserId column, matching how the query filters).
        modelBuilder.Entity<AIRecommendationHistory>(history =>
        {
            history.HasKey(a => a.Id);
            history.Property(a => a.Id).ValueGeneratedNever();
            history.Property(a => a.Model).HasMaxLength(AIRecommendationHistory.ModelMaxLength);
            history.Property(a => a.InputSummary).HasMaxLength(AIRecommendationHistory.SummaryMaxLength);
            history.Property(a => a.OutputSummary).HasMaxLength(AIRecommendationHistory.SummaryMaxLength);
            history.HasIndex(a => new { a.UserId, a.GeneratedAtUtc });
        });
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (RaceOnNextSave is { } inject)
        {
            RaceOnNextSave = null; // one-shot: only the first save is raced
            inject();              // a sibling context commits the conflicting row into the shared store
        }

        // Delegate to the REAL SQLite save. If the sibling just committed a row that collides with what we
        // staged, SQLite's unique index rejects our insert and EF surfaces a genuine DbUpdateException,
        // rolling this save back — exactly the concurrent-writer conflict the race paths must tolerate.
        return base.SaveChangesAsync(cancellationToken);
    }

    private static NotSupportedException Unsupported([CallerMemberName] string? member = null) =>
        new($"{member} is not mapped by TestAppDbContext (only Movie, Genre, MovieGenre, Watchlist, Review and " +
            "AIRecommendationHistory are).");
}
