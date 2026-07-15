namespace Cinora.Application.Common.Exceptions;

/// <summary>
/// Thrown when a requested entity does not exist. The Web layer's global exception handler maps this to
/// an HTTP 404 <c>ProblemDetails</c>, keeping "not found" handling out of individual handlers.
/// </summary>
public sealed class NotFoundException : Exception
{
    /// <summary>Initializes a new <see cref="NotFoundException"/> with a default message.</summary>
    public NotFoundException()
        : base("The requested resource was not found.")
    {
    }

    /// <summary>Initializes a new <see cref="NotFoundException"/> with the specified message.</summary>
    /// <param name="message">A human-readable description of what was not found.</param>
    public NotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="NotFoundException"/> with a message and inner exception.</summary>
    /// <param name="message">A human-readable description of what was not found.</param>
    /// <param name="innerException">The underlying exception that caused this one.</param>
    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a <see cref="NotFoundException"/> describing a missing entity by name and key, e.g.
    /// "Movie (42) was not found.".
    /// </summary>
    /// <param name="entityName">The entity type that was searched.</param>
    /// <param name="key">The key that produced no match.</param>
    /// <returns>A new exception with a formatted message.</returns>
    public static NotFoundException For(string entityName, object key) =>
        new($"{entityName} ({key}) was not found.");
}
