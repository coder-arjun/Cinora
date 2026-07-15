using FluentValidation.Results;

namespace Cinora.Application.Common.Exceptions;

/// <summary>
/// Thrown by the validation pipeline behavior when one or more FluentValidation rules fail, so an invalid
/// request never reaches its handler. Carries the failures grouped by property name; the Web layer's
/// global exception handler maps this to an HTTP 400 <c>ValidationProblemDetails</c>.
/// </summary>
public sealed class ValidationException : Exception
{
    /// <summary>Initializes an empty validation exception with no field errors.</summary>
    public ValidationException()
        : base("One or more validation failures have occurred.")
    {
        Errors = new Dictionary<string, string[]>();
    }

    /// <summary>Initializes the exception from a set of FluentValidation failures.</summary>
    /// <param name="failures">The failures to group by property name.</param>
    public ValidationException(IEnumerable<ValidationFailure> failures)
        : this()
    {
        ArgumentNullException.ThrowIfNull(failures);

        Errors = failures
            .GroupBy(failure => failure.PropertyName, failure => failure.ErrorMessage)
            .ToDictionary(
                group => group.Key,
                group => group.Distinct().ToArray());
    }

    /// <summary>
    /// The validation failures keyed by the offending property name; each value is the set of distinct
    /// error messages for that property. Shaped to populate <c>ValidationProblemDetails.Errors</c> directly.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
