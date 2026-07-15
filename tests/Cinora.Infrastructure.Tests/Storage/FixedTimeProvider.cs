namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// A controllable <see cref="TimeProvider"/> for deterministic expiry tests: <see cref="GetUtcNow"/> returns a
/// settable instant that tests can <see cref="Advance"/> past a token's bucketed expiry.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
