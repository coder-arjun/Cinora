using Cinora.Application.Common.Messaging;
using FluentValidation;
using ValidationException = Cinora.Application.Common.Exceptions.ValidationException;

namespace Cinora.Application.Common.Behaviors;

/// <summary>
/// Runs every registered FluentValidation <see cref="IValidator{T}"/> for the request before the handler.
/// If any rule fails it throws the Application <see cref="ValidationException"/> carrying the grouped
/// failures — one throw site that the Web global handler maps to HTTP 400. When a request has no
/// validators registered this behavior is a transparent no-op, so scaffolded requests pass straight through.
/// </summary>
/// <typeparam name="TRequest">The request being validated.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
/// <param name="validators">All validators registered for <typeparamref name="TRequest"/> (possibly none).</param>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Materialize once; the common Phase-1 case is zero validators, which must not allocate a context.
        var applicableValidators = validators as IReadOnlyCollection<IValidator<TRequest>>
            ?? validators.ToArray();

        if (applicableValidators.Count == 0)
        {
            return await next(cancellationToken);
        }

        var context = new ValidationContext<TRequest>(request);

        var results = await Task.WhenAll(
            applicableValidators.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        var failures = results
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToArray();

        if (failures.Length != 0)
        {
            throw new ValidationException(failures);
        }

        return await next(cancellationToken);
    }
}
