using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The Application-owned abstraction over Cinora's persistence. It exposes a <see cref="DbSet{TEntity}"/>
/// for each of the twelve domain entities plus <see cref="SaveChangesAsync(CancellationToken)"/>, so
/// handlers query and persist domain types without referencing EF Core's provider or any Identity type.
/// Implemented by <c>CinoraDbContext</c> in the Infrastructure layer. It deliberately never surfaces
/// <c>ApplicationUser</c> or other authentication types (ADR 0003).
/// </summary>
public interface IAppDbContext
{
    /// <summary>The profile/social user aggregates (never the Identity principal).</summary>
    DbSet<User> Users { get; }

    /// <summary>The directed friendship graph between users.</summary>
    DbSet<Friend> Friends { get; }

    /// <summary>The directed user-block graph (existence = blocked; §2).</summary>
    DbSet<UserBlock> UserBlocks { get; }

    /// <summary>The chat conversations (1:1 + group).</summary>
    DbSet<Conversation> Conversations { get; }

    /// <summary>The membership rows linking users to conversations.</summary>
    DbSet<ConversationMember> ConversationMembers { get; }

    /// <summary>The chat messages.</summary>
    DbSet<Message> Messages { get; }

    /// <summary>The catalogue of movies and series cached from TMDB.</summary>
    DbSet<Movie> Movies { get; }

    /// <summary>The TMDB genres titles can be tagged with.</summary>
    DbSet<Genre> Genres { get; }

    /// <summary>The many-to-many links between movies and genres.</summary>
    DbSet<MovieGenre> MovieGenres { get; }

    /// <summary>The user reviews of titles.</summary>
    DbSet<Review> Reviews { get; }

    /// <summary>The likes recorded against reviews.</summary>
    DbSet<ReviewLike> ReviewLikes { get; }

    /// <summary>The comments recorded against reviews.</summary>
    DbSet<Comment> Comments { get; }

    /// <summary>The users' watchlist entries.</summary>
    DbSet<Watchlist> Watchlists { get; }

    /// <summary>The in-app notifications delivered to users.</summary>
    DbSet<Notification> Notifications { get; }

    /// <summary>The registered Web Push device subscriptions.</summary>
    DbSet<Device> Devices { get; }

    /// <summary>The audit history of AI recommendation generations.</summary>
    DbSet<AIRecommendationHistory> AIRecommendationHistories { get; }

    /// <summary>Persists all pending changes to the underlying store.</summary>
    /// <param name="cancellationToken">A token to cancel the asynchronous save.</param>
    /// <returns>The number of state entries written to the store.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detaches ALL pending tracked entities from the change tracker — discarding every pending insert,
    /// update, and delete so that a subsequent <see cref="SaveChangesAsync(CancellationToken)"/> writes
    /// none of them. Use it to abandon staged work that a failed save left tracked (for example genres
    /// still <c>Added</c> after a unique-index race, which EF Core keeps <c>Added</c> after the failed
    /// save) before continuing with a fresh unit of work on the same context. Callers MUST ensure nothing
    /// they still intend to persist is currently pending, because it is ALL discarded wholesale.
    /// </summary>
    void DiscardPendingChanges();
}
