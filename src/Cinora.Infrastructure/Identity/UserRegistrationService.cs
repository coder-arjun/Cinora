using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Default <see cref="IUserRegistrationService"/> backed by <see cref="UserManager{TUser}"/> and the
/// shared <see cref="CinoraDbContext"/>. Both are resolved from the same request scope, so the store's
/// internal <c>SaveChanges</c> (invoked by <see cref="UserManager{TUser}.CreateAsync(TUser)"/>) runs on
/// the very context on which the explicit transaction is opened — it therefore enlists in that
/// transaction and does not commit until <c>CommitAsync</c> is called. A failure at any step rolls back
/// both the <c>AspNetUsers</c> row and the domain <c>Users</c> row (ADR 0003).
/// </summary>
internal sealed class UserRegistrationService(
    UserManager<ApplicationUser> userManager,
    CinoraDbContext dbContext) : IUserRegistrationService
{
    // F4: shown instead of Identity's "Email 'x' is already taken." so a duplicate registration does
    // not confirm an account exists (user enumeration).
    private const string DuplicateAccountMessage =
        "Registration could not be completed. Please try a different email or sign in.";

    // Display names are public (shown on profiles/reviews), so — unlike the email — revealing that one is taken
    // leaks nothing sensitive; a specific message is better UX so the user can pick another name.
    private const string DisplayNameTakenMessage =
        "That display name is already taken. Please choose a different one.";

    /// <inheritdoc />
    public async Task<UserRegistrationResult> RegisterAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Enforce display-name uniqueness up front for a friendly message; the unique NormalizedDisplayName
            // index is the ultimate guarantee (and the catch below covers the rare concurrent-registration race).
            var normalizedDisplayName = User.Normalize(displayName);
            var displayNameTaken = await dbContext.Set<User>()
                .AsNoTracking()
                .AnyAsync(u => u.NormalizedDisplayName == normalizedDisplayName, cancellationToken);
            if (displayNameTaken)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return UserRegistrationResult.Failure([DisplayNameTakenMessage]);
            }

            var applicationUser = new ApplicationUser { UserName = email, Email = email };

            var createResult = await userManager.CreateAsync(applicationUser, password);
            if (!createResult.Succeeded)
            {
                // CR3: roll back with a non-cancellable token so compensation always runs.
                await transaction.RollbackAsync(CancellationToken.None);

                // F4: a duplicate email (UserName == Email, so both DuplicateEmail and DuplicateUserName
                // fire) would otherwise leak account existence — collapse it to a single generic message.
                // Password-policy errors stay specific: they reveal nothing about whether the email exists.
                // Residual trade-off: the stronger "silent success + notify the address by email" pattern
                // needs email-confirmation sending, which is not built yet — until then a generic error is
                // the best available defence.
                var isDuplicateAccount = createResult.Errors.Any(
                    e => e.Code is "DuplicateEmail" or "DuplicateUserName");

                return isDuplicateAccount
                    ? UserRegistrationResult.Failure([DuplicateAccountMessage])
                    : UserRegistrationResult.Failure(createResult.Errors.Select(e => e.Description));
            }

            // Same shared PK as the just-created principal. User.Create validates the display name and
            // throws on violation — the catch below rolls the whole transaction back.
            dbContext.Set<User>().Add(User.Create(applicationUser.Id, displayName));
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return UserRegistrationResult.Success(applicationUser.Id);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyViolation(ex))
        {
            // A concurrent registration won the race on the unique NormalizedDisplayName index. Translate the
            // store violation into the same friendly, non-throwing failure the up-front pre-check returns.
            await transaction.RollbackAsync(CancellationToken.None);
            return UserRegistrationResult.Failure([DisplayNameTakenMessage]);
        }
        catch
        {
            // CR3: roll back with a non-cancellable token so compensation always runs, even if the
            // request's token was cancelled.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<UserRegistrationResult> RegisterExternalAsync(
        string loginProvider,
        string providerKey,
        string email,
        string displayName,
        bool emailVerified,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // F5: only mark the email confirmed when the provider explicitly asserted it is verified;
            // otherwise leave it unconfirmed (fail closed). A local account with the same email (if any)
            // makes CreateAsync fail on the unique-email rule — we never auto-link.
            var applicationUser = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = emailVerified,
            };

            var createResult = await userManager.CreateAsync(applicationUser);
            if (!createResult.Succeeded)
            {
                // CR3: non-cancellable rollback.
                await transaction.RollbackAsync(CancellationToken.None);
                return UserRegistrationResult.Failure(createResult.Errors.Select(e => e.Description));
            }

            var addLoginResult = await userManager.AddLoginAsync(
                applicationUser,
                new UserLoginInfo(loginProvider, providerKey, loginProvider));
            if (!addLoginResult.Succeeded)
            {
                // CR3: non-cancellable rollback.
                await transaction.RollbackAsync(CancellationToken.None);
                return UserRegistrationResult.Failure(addLoginResult.Errors.Select(e => e.Description));
            }

            // The external display name was not chosen by the user, so make it unique automatically rather than
            // failing the sign-in on a collision with an existing NormalizedDisplayName.
            var uniqueDisplayName = await EnsureUniqueDisplayNameAsync(displayName, cancellationToken);
            dbContext.Set<User>().Add(User.Create(applicationUser.Id, uniqueDisplayName));
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return UserRegistrationResult.Success(applicationUser.Id);
        }
        catch
        {
            // CR3: non-cancellable rollback so compensation always runs.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // Returns the display name unchanged when its normalized form is free; otherwise appends a short unique
    // suffix (external names are provider-supplied, so we must not fail the sign-in on a collision).
    private async Task<string> EnsureUniqueDisplayNameAsync(string displayName, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(displayName);
        var taken = await dbContext.Set<User>()
            .AsNoTracking()
            .AnyAsync(u => u.NormalizedDisplayName == normalized, cancellationToken);
        if (!taken)
        {
            return displayName;
        }

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var maxBaseLength = User.DisplayNameMaxLength - suffix.Length - 1;
        var baseName = displayName.Length <= maxBaseLength ? displayName : displayName[..maxBaseLength];
        return $"{baseName} {suffix}";
    }

    // A SQL Server unique-constraint / unique-index violation (2627 / 2601). At the domain-User save step the
    // only unique index is IX_Users_NormalizedDisplayName (the email dup is caught earlier by CreateAsync), so a
    // hit here means a concurrent registration took the same display name.
    private static bool IsDuplicateKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2627 or 2601 };
}
