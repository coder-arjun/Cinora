using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for Cinora. It derives from
/// <see cref="IdentityDbContext{TUser, TRole, TKey}"/> so ASP.NET Identity and the domain schema
/// share one database and one transaction (registration creates the Identity principal and the
/// domain <see cref="User"/> atomically — ADR 0003), and it implements <see cref="IAppDbContext"/>
/// so the Application layer works against domain types only.
/// </summary>
/// <remarks>
/// The inherited Identity <c>Users</c> property is a <see cref="DbSet{ApplicationUser}"/>; the
/// domain user set is therefore exposed through the <see cref="IAppDbContext.Users"/> explicit
/// interface implementation to avoid hiding it. All entity mappings live in
/// <c>Persistence/Configurations</c> and are applied by assembly scan.
/// </remarks>
public sealed class CinoraDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IAppDbContext, IDataProtectionKeyContext
{
    /// <summary>Initializes a new instance of the <see cref="CinoraDbContext"/> class.</summary>
    /// <param name="options">The context options configured by the composition root (provider and connection string).</param>
    public CinoraDbContext(DbContextOptions<CinoraDbContext> options)
        : base(options)
    {
    }

    /// <summary>The directed friendship graph between users.</summary>
    public DbSet<Friend> Friends => Set<Friend>();

    /// <summary>The directed user-block graph (existence = blocked; §2).</summary>
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    /// <summary>The chat conversations (1:1 + group).</summary>
    public DbSet<Conversation> Conversations => Set<Conversation>();

    /// <summary>The membership rows linking users to conversations.</summary>
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    /// <summary>The chat messages.</summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <summary>The catalogue of movies and series cached from TMDB.</summary>
    public DbSet<Movie> Movies => Set<Movie>();

    /// <summary>The TMDB genres titles can be tagged with.</summary>
    public DbSet<Genre> Genres => Set<Genre>();

    /// <summary>The many-to-many links between movies and genres.</summary>
    public DbSet<MovieGenre> MovieGenres => Set<MovieGenre>();

    /// <summary>The user reviews of titles.</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>The likes recorded against reviews.</summary>
    public DbSet<ReviewLike> ReviewLikes => Set<ReviewLike>();

    /// <summary>The comments recorded against reviews.</summary>
    public DbSet<Comment> Comments => Set<Comment>();

    /// <summary>The users' watchlist entries.</summary>
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();

    /// <summary>The in-app notifications delivered to users.</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>The registered Web Push device subscriptions.</summary>
    public DbSet<Device> Devices => Set<Device>();

    /// <summary>The audit history of AI recommendation generations.</summary>
    public DbSet<AIRecommendationHistory> AIRecommendationHistories => Set<AIRecommendationHistory>();

    /// <summary>The ASP.NET Data-Protection key ring, persisted here so the auth cookie + anti-forgery tokens
    /// survive app restarts/redeploys (a regenerated key ring silently logs everyone out).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Explicit implementation: the base IdentityDbContext already exposes a public `Users` set of
    // ApplicationUser. This surfaces the DOMAIN user set to the Application layer without hiding it.
    DbSet<User> IAppDbContext.Users => Set<User>();

    /// <inheritdoc />
    // Delegates to EF Core's change tracker: Clear() detaches every tracked entity in one pass, discarding
    // all pending inserts/updates/deletes so the next SaveChanges writes none of them.
    public void DiscardPendingChanges() => ChangeTracker.Clear();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity's own mappings (AspNetUsers, roles, claims, tokens, …) must be applied first.
        base.OnModelCreating(builder);

        // Then apply every IEntityTypeConfiguration<T> in this assembly (internal configs included).
        builder.ApplyConfigurationsFromAssembly(typeof(CinoraDbContext).Assembly);
    }
}
