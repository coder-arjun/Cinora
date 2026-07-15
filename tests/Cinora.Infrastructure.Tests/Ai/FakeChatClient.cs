using Microsoft.Extensions.AI;

namespace Cinora.Infrastructure.Tests.Ai;

/// <summary>
/// A hand-rolled fake <see cref="IChatClient"/> for the <see cref="Cinora.Infrastructure.Ai.OllamaRecommendationEngine"/>
/// tests (mirroring <c>FakeTmdbClient</c> — no NSubstitute, no live Ollama). It captures the messages and
/// options the adapter sent — so a test can assert the prompt's content and hygiene — and returns a canned
/// <see cref="ChatResponse"/> or throws/blocks per the supplied delegate. No network, no model.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, CancellationToken, Task<ChatResponse>> _respond;

    /// <summary>Creates the fake with a delegate that produces (or faults) the response for each call.</summary>
    /// <param name="respond">Given the sent messages and the cancellation token, returns the canned response.</param>
    public FakeChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<ChatResponse>> respond) =>
        _respond = respond;

    /// <summary>The messages the adapter passed on the most recent call (the system + user prompt).</summary>
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>The options the adapter passed on the most recent call.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>The number of times the adapter invoked the client.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMessages = messages.ToList();
        LastOptions = options;
        return _respond(LastMessages, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streaming is not used by the recommendation engine.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
