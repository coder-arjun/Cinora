using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Profiles;

/// <summary>
/// Loads the CURRENT user's own editable settings to pre-fill the settings form (§4.2) — a tiny owner-only read
/// distinct from <see cref="GetProfileQuery"/> (which shapes a viewer-relative PUBLIC profile). The owner is
/// resolved server-side from <see cref="ICurrentUser"/>; no id is bound.
/// </summary>
public sealed record GetMyProfileSettingsQuery : IRequest<MyProfileSettingsVm>;

/// <summary>The current user's editable profile settings.</summary>
/// <param name="DisplayName">The user's current display name.</param>
/// <param name="IsProfilePublic">Whether the profile is public (<c>true</c>) or friends-only (<c>false</c>).</param>
/// <param name="AvatarFileKey">The user's avatar storage key, or <c>null</c> when none is set (monogram).</param>
/// <param name="DefaultLanguage">The preferred movie language (lower-case ISO-639-1), or <c>null</c> for Global.</param>
public sealed record MyProfileSettingsVm(string DisplayName, bool IsProfilePublic, string? AvatarFileKey, string? DefaultLanguage);

/// <summary>
/// Handles <see cref="GetMyProfileSettingsQuery"/> with a single <c>AsNoTracking</c> projection of the current
/// user's row. A missing row (impossible behind <c>[Authorize]</c> given atomic registration) is a defensive 404.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (§2, ADR 0009).</param>
public sealed class GetMyProfileSettingsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyProfileSettingsQuery, MyProfileSettingsVm>
{
    /// <inheritdoc />
    public async Task<MyProfileSettingsVm> Handle(
        GetMyProfileSettingsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new MyProfileSettingsVm(user.DisplayName, user.IsProfilePublic, user.AvatarFileKey, user.DefaultLanguage))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User ({userId}) was not found.");
    }
}
