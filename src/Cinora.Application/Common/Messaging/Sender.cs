using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Application.Common.Messaging;

/// <summary>
/// Cinora's hand-rolled mediator dispatcher. For a runtime request type it resolves the single
/// <see cref="IRequestHandler{TRequest, TResponse}"/> and the ordered
/// <see cref="IPipelineBehavior{TRequest, TResponse}"/>s from the request scope, then composes the
/// behaviors as an onion around the handler and invokes it. A small typed wrapper per request type
/// bridges the non-generic <see cref="Send{TResponse}"/> call to the strongly typed handler; wrappers
/// are cached so the reflection cost is paid once per request type.
/// </summary>
/// <param name="serviceProvider">The (scoped) provider used to resolve handlers and behaviors.</param>
public sealed class Sender(IServiceProvider serviceProvider) : ISender
{
    // Keyed by concrete request type → the typed wrapper instance (stored as object because the wrapper's
    // TResponse varies). A given request type closes IRequest<TResponse> once, so the cast in Send is safe.
    private static readonly ConcurrentDictionary<Type, object> WrapperCache = new();

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)WrapperCache.GetOrAdd(
            request.GetType(),
            requestType =>
            {
                var wrapperType = typeof(RequestHandlerWrapperImpl<,>)
                    .MakeGenericType(requestType, typeof(TResponse));
                return Activator.CreateInstance(wrapperType)
                    ?? throw new InvalidOperationException(
                        $"Unable to create a mediator wrapper for request type '{requestType}'.");
            });

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }
}

/// <summary>
/// Non-generic-over-request base so <see cref="Sender"/> can cache and invoke a wrapper knowing only the
/// response type. The concrete implementation binds the request type at construction.
/// </summary>
/// <typeparam name="TResponse">The response the wrapped request produces.</typeparam>
internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// Binds a specific request type to its handler and behaviors, composing the behavior chain around the
/// handler. Behaviors are folded in reverse registration order so the first-registered behavior ends up
/// outermost (Logging → Validation → Performance → handler).
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response the request produces.</typeparam>
internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

        RequestHandlerDelegate<TResponse> next = ct => handler.Handle(typedRequest, ct);

        // Registration order is [Logging, Validation, Performance]; reversing and folding makes Logging
        // the outermost wrapper, so it runs first and the handler runs last.
        var behaviors = serviceProvider
            .GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .Reverse()
            .ToArray();

        foreach (var behavior in behaviors)
        {
            var current = next;
            var currentBehavior = behavior;
            next = ct => currentBehavior.Handle(typedRequest, current, ct);
        }

        return next(cancellationToken);
    }
}
