using Serilog.Core;
using Serilog.Events;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A thread-safe in-memory Serilog sink used by tests to assert that specific log events (notably the
/// request-completion event emitted by <c>UseSerilogRequestLogging</c>) are produced. It is registered as
/// an <see cref="ILogEventSink"/> in the test host's DI container and wired into the logger via
/// <c>ReadFrom.Services</c>, so it observes exactly what the running app logs — without touching the file system.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];
    private readonly Lock _gate = new();

    /// <summary>Records an emitted log event.</summary>
    /// <param name="logEvent">The event to capture.</param>
    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }

    /// <summary>Returns a point-in-time copy of the captured events.</summary>
    /// <returns>A snapshot list of the events captured so far.</returns>
    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    /// <summary>Discards all captured events so a test starts from a clean slate.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}
