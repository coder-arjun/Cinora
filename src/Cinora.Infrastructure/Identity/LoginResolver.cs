using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Default <see cref="ILoginResolver"/>. An identifier containing '@' is treated as an email and resolved on
/// the unique email; anything else is treated as a display name and resolved via the unique, case-insensitive
/// <see cref="User.NormalizedDisplayName"/>, whose shared primary key maps to the Identity principal. Both
/// branches return <c>null</c> on no match so the controller shows a single generic failure (no enumeration).
/// </summary>
internal sealed class LoginResolver(
    UserManager<ApplicationUser> userManager,
    CinoraDbContext dbContext) : ILoginResolver
{
    /// <inheritdoc />
    public async Task<string?> ResolveUserNameAsync(string emailOrDisplayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(emailOrDisplayName))
        {
            return null;
        }

        var identifier = emailOrDisplayName.Trim();

        // An '@' means the user typed an email — resolve on the unique email (UserName == Email).
        if (identifier.Contains('@', StringComparison.Ordinal))
        {
            var byEmail = await userManager.FindByEmailAsync(identifier);
            return byEmail?.UserName;
        }

        // Otherwise treat it as a display name: resolve via the unique, case-insensitive NormalizedDisplayName,
        // then map the shared primary key to the Identity principal's UserName for PasswordSignInAsync.
        var normalized = User.Normalize(identifier);
        var userId = await dbContext.Set<User>()
            .AsNoTracking()
            .Where(u => u.NormalizedDisplayName == normalized)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == Guid.Empty)
        {
            return null;
        }

        var appUser = await userManager.FindByIdAsync(userId.ToString());
        return appUser?.UserName;
    }
}
