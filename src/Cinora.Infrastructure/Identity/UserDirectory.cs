using Cinora.Application.Common.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Identity-backed <see cref="IUserDirectory"/>: exact email lookup via <see cref="UserManager{TUser}"/>, which
/// normalizes the input and seeks the indexed <c>NormalizedEmail</c>. Returns the shared <see cref="Guid"/> key
/// that also identifies the domain <c>User</c> (ADR 0003).
/// </summary>
/// <param name="userManager">The Identity user manager.</param>
public sealed class UserDirectory(UserManager<ApplicationUser> userManager) : IUserDirectory
{
    /// <inheritdoc />
    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var user = await userManager.FindByEmailAsync(email);
        return user?.Id;
    }
}
