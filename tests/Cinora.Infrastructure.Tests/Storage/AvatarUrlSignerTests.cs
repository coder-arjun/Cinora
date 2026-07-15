using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Storage;

namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// Verifies the deterministic, bucketed-expiry avatar token (the SAS stand-in, ADR 0013 §2.4): a minted token
/// round-trips, is byte-identical within its bucket (browser-cacheable), and is rejected when tampered or
/// expired. Time is driven by a <see cref="FixedTimeProvider"/> so expiry is deterministic.
/// </summary>
public sealed class AvatarUrlSignerTests
{
    private const string Key = "avatars/0123456789abcdef0123456789abcdef.jpg";

    // A round instant well inside a 24h bucket window (unix bucket boundary at 1,036,800).
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static AvatarUrlSigner CreateSigner(FixedTimeProvider time) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new FileStorageOptions
            {
                UrlSigningKey = "unit-test-signing-key",
                SignedUrlBucketHours = 24,
            }),
            time);

    [Fact]
    public void Mint_then_validate_round_trips_the_key()
    {
        var signer = CreateSigner(new FixedTimeProvider(Now));

        var token = signer.CreateToken(Key, TimeSpan.FromHours(1));

        Assert.True(signer.TryValidateToken(token, out var resolved));
        Assert.Equal(Key, resolved);
    }

    [Fact]
    public void Tokens_are_byte_identical_within_the_same_bucket()
    {
        var time = new FixedTimeProvider(Now);
        var signer = CreateSigner(time);

        var first = signer.CreateToken(Key, TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromMinutes(1));
        var second = signer.CreateToken(Key, TimeSpan.FromHours(1));

        // Deterministic HMAC + bucketed expiry ⇒ the URL is stable across renders in the same window.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Validate_rejects_a_tampered_signature()
    {
        var signer = CreateSigner(new FixedTimeProvider(Now));
        var token = signer.CreateToken(Key, TimeSpan.FromHours(1));

        // Flip the last character of the signature segment.
        var lastChar = token[^1];
        var mutated = token[..^1] + (lastChar == 'A' ? 'B' : 'A');

        Assert.False(signer.TryValidateToken(mutated, out _));
    }

    [Fact]
    public void Validate_rejects_a_token_signed_with_a_different_key()
    {
        var minted = CreateSigner(new FixedTimeProvider(Now)).CreateToken(Key, TimeSpan.FromHours(1));

        var otherSigner = new AvatarUrlSigner(
            Microsoft.Extensions.Options.Options.Create(new FileStorageOptions { UrlSigningKey = "a-completely-different-key" }),
            new FixedTimeProvider(Now));

        Assert.False(otherSigner.TryValidateToken(minted, out _));
    }

    [Fact]
    public void Validate_rejects_an_expired_token()
    {
        var time = new FixedTimeProvider(Now);
        var signer = CreateSigner(time);
        var token = signer.CreateToken(Key, TimeSpan.FromHours(1));

        // Advance well past the (≤ 24h) bucketed expiry.
        time.Advance(TimeSpan.FromDays(2));

        Assert.False(signer.TryValidateToken(token, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only-one-part")]
    [InlineData(".")]
    public void Validate_rejects_a_malformed_token(string token)
    {
        var signer = CreateSigner(new FixedTimeProvider(Now));

        Assert.False(signer.TryValidateToken(token, out _));
    }
}
