using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Discovery;

/// <summary>
/// Reads the CURRENT user's saved default movie-language preference (a lower-case ISO-639-1 code, or <c>null</c>
/// for Global). Used by the Discovery shell to open in the user's preferred language when the request carries no
/// explicit <c>?language=</c> choice. The owner is resolved server-side from <see cref="ICurrentUser"/>; no id
/// is bound. Only dispatched on the authenticated browse path.
/// </summary>
public sealed record GetMyDefaultLanguageQuery : IRequest<string?>;

/// <summary>
/// Handles <see cref="GetMyDefaultLanguageQuery"/> with a single <c>AsNoTracking</c> scalar projection of the
/// current user's <c>DefaultLanguage</c> column. A missing row yields <c>null</c> (Global) rather than throwing —
/// the discovery shell simply falls back to Global.
/// </summary>
/// <param name="db">The persistence context — direct set access, no repository.</param>
/// <param name="currentUser">The server-resolved acting user (ADR 0009).</param>
public sealed class GetMyDefaultLanguageQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyDefaultLanguageQuery, string?>
{
    /// <inheritdoc />
    public async Task<string?> Handle(GetMyDefaultLanguageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.GetRequiredUserId();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.DefaultLanguage)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
