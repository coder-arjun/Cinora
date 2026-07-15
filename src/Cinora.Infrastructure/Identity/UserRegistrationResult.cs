namespace Cinora.Infrastructure.Identity;

/// <summary>
/// The outcome of an atomic user-registration attempt in which the Identity principal
/// (<see cref="ApplicationUser"/>) and the shared-primary-key domain
/// <see cref="Cinora.Domain.Entities.User"/> are created in a single transaction. On success it
/// carries the shared identifier; on failure it carries the human-readable Identity error
/// descriptions and no partial state was persisted (ADR 0003).
/// </summary>
public sealed class UserRegistrationResult
{
    private UserRegistrationResult(bool succeeded, Guid userId, IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        UserId = userId;
        Errors = errors;
    }

    /// <summary>Whether registration succeeded and both rows were committed together.</summary>
    public bool Succeeded { get; }

    /// <summary>
    /// The identifier shared by the Identity principal and the domain user;
    /// <see cref="Guid.Empty"/> when <see cref="Succeeded"/> is <c>false</c>.
    /// </summary>
    public Guid UserId { get; }

    /// <summary>The registration error descriptions; empty when <see cref="Succeeded"/> is <c>true</c>.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates a successful result for the given committed shared identifier.</summary>
    /// <param name="userId">The committed shared primary key.</param>
    /// <returns>A successful <see cref="UserRegistrationResult"/>.</returns>
    public static UserRegistrationResult Success(Guid userId) => new(true, userId, []);

    /// <summary>Creates a failed result carrying the supplied error descriptions.</summary>
    /// <param name="errors">The human-readable reasons registration failed.</param>
    /// <returns>A failed <see cref="UserRegistrationResult"/>.</returns>
    public static UserRegistrationResult Failure(IEnumerable<string> errors) =>
        new(false, Guid.Empty, [.. errors]);
}
