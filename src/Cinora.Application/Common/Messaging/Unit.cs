namespace Cinora.Application.Common.Messaging;

/// <summary>
/// The response type for a command that produces no value — the hand-rolled mediator's equivalent of
/// <c>void</c> (every <see cref="IRequest{TResponse}"/> must close a response type, so a command with nothing
/// meaningful to return uses <see cref="Unit"/>). A zero-size <see langword="readonly"/> record struct with a
/// single canonical <see cref="Value"/>; handlers return <c>Unit.Value</c>.
/// </summary>
public readonly record struct Unit
{
    /// <summary>The single canonical <see cref="Unit"/> value handlers return.</summary>
    public static Unit Value => default;
}
