using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Common;

/// <summary>
/// Internal guard helpers that centralize the small, repeated invariant checks used by the
/// domain factory and behaviour methods. Kept <c>internal</c> so it is not part of the public
/// domain surface. Every failure surfaces as a <see cref="DomainException"/>.
/// </summary>
internal static class Guard
{
    /// <summary>Ensures a string is neither null, empty, nor whitespace and returns it.</summary>
    internal static string Required(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{paramName} is required.");
        }

        return value;
    }

    /// <summary>Ensures a string is required (see <see cref="Required(string?, string)"/>) and within a maximum length.</summary>
    internal static string Required(string? value, int maxLength, string paramName)
    {
        var result = Required(value, paramName);
        if (result.Length > maxLength)
        {
            throw new DomainException($"{paramName} exceeds its maximum allowed length.");
        }

        return result;
    }

    /// <summary>Ensures an integer is strictly greater than zero and returns it.</summary>
    internal static int Positive(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new DomainException($"{paramName} must be greater than zero.");
        }

        return value;
    }

    /// <summary>Ensures an integer is zero or greater and returns it.</summary>
    internal static int NonNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new DomainException($"{paramName} must not be negative.");
        }

        return value;
    }
}
