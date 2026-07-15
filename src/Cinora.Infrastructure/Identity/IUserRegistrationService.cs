namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Creates a Cinora user atomically: the ASP.NET Identity principal (<see cref="ApplicationUser"/>)
/// and the shared-primary-key domain <see cref="Cinora.Domain.Entities.User"/> are written inside a
/// single database transaction, so a failure at any step rolls back both and never leaves an orphan
/// <c>AspNetUsers</c> row (ADR 0003; the Milestone 1.3 blocking requirement). This is the seam the
/// registration flows depend on and integration tests exercise for orphan prevention.
/// </summary>
public interface IUserRegistrationService
{
    /// <summary>Registers a local email/password account.</summary>
    /// <param name="email">The account email address, also used as the sign-in user name.</param>
    /// <param name="password">The plaintext password, validated against the Identity password policy.</param>
    /// <param name="displayName">The public display name for the domain profile.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="UserRegistrationResult"/> describing success (with the shared id) or the Identity
    /// failures. Throws if an unexpected error occurs after the principal is created — the transaction
    /// is rolled back first so no orphan row remains.
    /// </returns>
    Task<UserRegistrationResult> RegisterAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken);

    /// <summary>Registers a first-time external (e.g. Google) account and links the external login.</summary>
    /// <param name="loginProvider">The external provider name (e.g. <c>Google</c>).</param>
    /// <param name="providerKey">The provider's stable user key for this account.</param>
    /// <param name="email">The email asserted by the provider; also used as the sign-in user name.</param>
    /// <param name="displayName">The public display name seeded from the provider profile.</param>
    /// <param name="emailVerified">
    /// Whether the provider explicitly asserted the email is verified. When <c>true</c> the created
    /// account's email is marked confirmed; otherwise it stays unconfirmed (fail closed).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="UserRegistrationResult"/> describing success (with the shared id) or the Identity
    /// failures. Rolls back on any failure so no orphan row remains.
    /// </returns>
    Task<UserRegistrationResult> RegisterExternalAsync(
        string loginProvider,
        string providerKey,
        string email,
        string displayName,
        bool emailVerified,
        CancellationToken cancellationToken);
}
