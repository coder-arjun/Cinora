namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Resolves a user-supplied login identifier — an email OR a display name — to the Identity principal's
/// <c>UserName</c> so <see cref="Microsoft.AspNetCore.Identity.SignInManager{TUser}"/> can complete the
/// password sign-in. Display names are matched case-insensitively via the unique normalized handle. Returns
/// <c>null</c> when no account matches, so the caller can surface one generic error (no user enumeration).
/// </summary>
public interface ILoginResolver
{
    /// <summary>Resolves an email-or-display-name to the matching account's Identity user name.</summary>
    /// <param name="emailOrDisplayName">The raw identifier the user typed on the sign-in form.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The Identity <c>UserName</c> to sign in with, or <c>null</c> when no account matches.</returns>
    Task<string?> ResolveUserNameAsync(string emailOrDisplayName, CancellationToken cancellationToken);
}
