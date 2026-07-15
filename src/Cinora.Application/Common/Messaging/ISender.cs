namespace Cinora.Application.Common.Messaging;

/// <summary>
/// The entry point to Cinora's hand-rolled mediator. Controllers, hubs, and jobs depend on this
/// abstraction to dispatch a request to its handler through the registered pipeline behaviors, without
/// referencing the concrete <c>Sender</c> or any third-party mediator library.
/// </summary>
public interface ISender
{
    /// <summary>Sends a request through the pipeline to its single handler and returns the response.</summary>
    /// <typeparam name="TResponse">The response type the request produces.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The response produced by the handler after all behaviors have run.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
