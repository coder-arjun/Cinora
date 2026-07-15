namespace Cinora.Application.Common.Messaging;

/// <summary>
/// Handles a single <see cref="IRequest{TResponse}"/> use case. Exactly one implementation is registered
/// per request type; <see cref="ISender"/> resolves and invokes it after the pipeline behaviors run.
/// </summary>
/// <typeparam name="TRequest">The request type this handler services.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Executes the use case for the given request.</summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The response produced by handling the request.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
