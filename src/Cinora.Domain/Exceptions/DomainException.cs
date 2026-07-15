namespace Cinora.Domain.Exceptions;

/// <summary>
/// Raised when a domain invariant is violated — for example an out-of-range rating, an invalid
/// state transition, or a missing required value. The Application layer maps this exception to a
/// validation (HTTP 400) response at its boundary; it never leaks a stack trace to the client.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>Initializes a new <see cref="DomainException"/> with a default message.</summary>
    public DomainException()
        : base("A domain invariant was violated.")
    {
    }

    /// <summary>Initializes a new <see cref="DomainException"/> with the specified message.</summary>
    /// <param name="message">A human-readable description of the invariant that was violated.</param>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="DomainException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable description of the invariant that was violated.</param>
    /// <param name="innerException">The underlying exception that caused this one.</param>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
