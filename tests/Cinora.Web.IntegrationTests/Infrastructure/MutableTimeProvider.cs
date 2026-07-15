namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A controllable <see cref="TimeProvider"/> the integration tests inject (via <c>ConfigureTestServices</c>) to
/// drive the avatar URL signer's clock deterministically — e.g. minting a token, then advancing past its
/// bucketed expiry to prove an expired token is rejected with a 404.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private long _utcTicks;

    public MutableTimeProvider(DateTimeOffset now) => _utcTicks = now.UtcTicks;

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}
