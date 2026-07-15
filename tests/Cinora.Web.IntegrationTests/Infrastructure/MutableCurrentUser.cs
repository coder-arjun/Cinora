using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A test <see cref="ICurrentUser"/> whose acting user is settable, so a suite can dispatch an owner-scoped
/// query (e.g. <c>GetMyRecommendationsQuery</c>) through <c>ISender</c> from a background scope where there is no
/// HTTP request (and the real <c>CurrentUser</c> would resolve anonymous). Registered as a singleton via
/// <c>ConfigureTestServices</c>; set <see cref="UserId"/> before each dispatch to choose the actor.
/// </summary>
internal sealed class MutableCurrentUser : ICurrentUser
{
    /// <summary>The acting user's id, or <c>null</c> to model an anonymous request.</summary>
    public Guid? UserId { get; set; }

    /// <inheritdoc />
    public bool IsAuthenticated => UserId is not null;

    /// <inheritdoc />
    public Guid GetRequiredUserId() =>
        UserId ?? throw new ForbiddenAccessException("No current user was set for the test dispatch.");
}
