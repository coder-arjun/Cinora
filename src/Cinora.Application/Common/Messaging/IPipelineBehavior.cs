using System.Diagnostics.CodeAnalysis;

namespace Cinora.Application.Common.Messaging;

/// <summary>
/// Continues the request pipeline — invoking the next behavior, or ultimately the
/// <see cref="IRequestHandler{TRequest, TResponse}"/> — and returns its response.
/// </summary>
/// <typeparam name="TResponse">The response the pipeline produces.</typeparam>
/// <param name="cancellationToken">A token to observe for cancellation.</param>
/// <returns>The response from the remainder of the pipeline.</returns>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "RequestHandlerDelegate is the established mediator continuation type name and is a " +
                    "deliberate part of the public pipeline contract (Milestone 1.4 brief).")]
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken);

/// <summary>
/// A cross-cutting step that wraps request handling — logging, validation, timing, and similar concerns
/// written once and applied to every request. Behaviors form an onion around the handler in the order
/// they are registered (outermost first). Business rules never live here; those belong in the Domain.
/// </summary>
/// <typeparam name="TRequest">The request type flowing through the pipeline.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Runs this behavior, calling <paramref name="next"/> to continue the pipeline.</summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">The continuation that invokes the next behavior or the handler.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The response produced by the remainder of the pipeline.</returns>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "'next' is the conventional mediator continuation parameter name; the pipeline is " +
                        "consumed only from C#.")]
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
