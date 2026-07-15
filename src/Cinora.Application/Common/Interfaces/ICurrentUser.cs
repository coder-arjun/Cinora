namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// The server-resolved identity of the user making the current request (ADR 0009). Write handlers obtain the
/// acting user's id from here — <b>never</b> from a client-bound command field, which would be a spoofing
/// vector (`security-hardening` rule). Commands carry only <em>resource</em> ids (a review id, a movie id);
/// the actor is always resolved server-side through this seam.
/// </summary>
public interface ICurrentUser
{
    /// <summary>The current user's id, or <c>null</c> when the request is anonymous.</summary>
    Guid? UserId { get; }

    /// <summary>Whether the current request is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Returns the current user's id, or throws when the request is anonymous. Use inside write handlers that
    /// run behind an <c>[Authorize]</c> endpoint, where authentication is already guaranteed by the pipeline —
    /// the throw is a defensive backstop, mapped to a 403 by the global exception handler.
    /// </summary>
    /// <returns>The authenticated user's id.</returns>
    /// <exception cref="Exceptions.ForbiddenAccessException">Thrown when the request is anonymous.</exception>
    Guid GetRequiredUserId();
}
