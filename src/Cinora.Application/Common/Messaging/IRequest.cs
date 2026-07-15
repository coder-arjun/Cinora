namespace Cinora.Application.Common.Messaging;

/// <summary>
/// Marks a request (command or query) that, when sent through <see cref="ISender"/>, is handled by a
/// single <see cref="IRequestHandler{TRequest, TResponse}"/> and produces a <typeparamref name="TResponse"/>.
/// This is Cinora's hand-rolled mediator contract — deliberately independent of any third-party mediator
/// package so the Application layer carries no commercial-licensed dependency.
/// </summary>
/// <typeparam name="TResponse">The type returned when the request is handled.</typeparam>
public interface IRequest<out TResponse>;
