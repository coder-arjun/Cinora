namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// Resolves a user by an authentication attribute that lives ONLY on the Identity principal (ADR 0003) — today,
/// the email. It keeps the Application layer's exact-match user search (§4) free of any Identity type. Exact
/// match only; returns <c>null</c> when no user has that email.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Returns the id of the user whose email matches <paramref name="email"/> exactly (case-insensitive),
    /// or <c>null</c> when none.</summary>
    /// <param name="email">The full email address to look up.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);
}
