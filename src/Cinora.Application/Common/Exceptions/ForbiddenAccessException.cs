namespace Cinora.Application.Common.Exceptions;

/// <summary>
/// Thrown when the current user is authenticated but not permitted to act on the target resource — for
/// example editing or deleting another user's review or comment, or responding to a friend request addressed
/// to someone else. The Web layer's global exception handler maps this to an HTTP 403 <c>ProblemDetails</c>.
/// Ownership is enforced inside the command handler (inside the unit of work), never only in the UI — a hidden
/// edit/delete button is presentation sugar, not the gate (ADR 0009).
/// </summary>
public sealed class ForbiddenAccessException : Exception
{
    /// <summary>Initializes a new <see cref="ForbiddenAccessException"/> with a default message.</summary>
    public ForbiddenAccessException()
        : base("You are not allowed to perform this action.")
    {
    }

    /// <summary>Initializes a new <see cref="ForbiddenAccessException"/> with the specified message.</summary>
    /// <param name="message">A human-readable description of why access was denied.</param>
    public ForbiddenAccessException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="ForbiddenAccessException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable description of why access was denied.</param>
    /// <param name="innerException">The underlying exception that caused this one.</param>
    public ForbiddenAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
