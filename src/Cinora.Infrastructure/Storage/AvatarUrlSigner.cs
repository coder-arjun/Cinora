using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Storage;

/// <summary>
/// Mints and verifies the deterministic, time-limited avatar-serving token — the free local stand-in for a
/// Blob SAS (ADR 0013 §2.4). The token carries the storage key and a coarse <b>bucketed</b> expiry, signed with
/// an <b>HMAC-SHA256</b> over <c>{storageKey}|{expiryUnix}</c> using <see cref="FileStorageOptions.UrlSigningKey"/>.
/// The HMAC is deterministic (unlike <c>ITimeLimitedDataProtector</c>), so a key's URL is byte-identical within
/// its bucket window and browser-cacheable across a feed, yet rotates and expires each bucket. A holder of a
/// token cannot forge a different key or a later expiry without the signing key. Time is read from an injected
/// <see cref="TimeProvider"/> so expiry is deterministically testable.
/// </summary>
internal sealed class AvatarUrlSigner
{
    private const int SecondsPerHour = 3600;

    private readonly IOptions<FileStorageOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the signer over the bound options and a time source.</summary>
    /// <param name="options">The file-storage options supplying the signing key and bucket size.</param>
    /// <param name="timeProvider">The clock used for minting and expiry checks (injectable for tests).</param>
    public AvatarUrlSigner(IOptions<FileStorageOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a signed token whose bucketed expiry is at least <paramref name="ttl"/> in the future. The
    /// effective expiry is rounded UP to the next bucket boundary (<see cref="FileStorageOptions.SignedUrlBucketHours"/>),
    /// making the token byte-identical for every render whose <c>now + ttl</c> falls in the same bucket.
    /// </summary>
    /// <param name="storageKey">The (valid) storage key the token grants access to.</param>
    /// <param name="ttl">The minimum validity window before the bucketed expiry.</param>
    /// <returns>A URL-safe token of the form <c>base64url(payload).base64url(hmac)</c>.</returns>
    public string CreateToken(string storageKey, TimeSpan ttl)
    {
        var options = _options.Value;
        var bucketSeconds = Math.Max(1, options.SignedUrlBucketHours) * (long)SecondsPerHour;

        var nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var ttlSeconds = (long)Math.Max(0d, ttl.TotalSeconds);
        var minimumExpiry = nowUnix + ttlSeconds;
        var expiryUnix = CeilingToMultiple(minimumExpiry, bucketSeconds);

        var payload = FormatPayload(storageKey, expiryUnix);
        var signature = ComputeSignature(payload, options.UrlSigningKey);
        return $"{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.{signature}";
    }

    /// <summary>
    /// Verifies a token: recomputes the HMAC in fixed time, rejects a signature mismatch or a past expiry, and
    /// extracts the storage key. Returns <see langword="false"/> for any malformed, tampered, or expired token.
    /// </summary>
    /// <param name="token">The token from the query string.</param>
    /// <param name="storageKey">The extracted storage key when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for a valid, unexpired, untampered token; otherwise <see langword="false"/>.</returns>
    public bool TryValidateToken(string? token, out string storageKey)
    {
        storageKey = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        var encodedPayload = token[..dot];
        var providedSignature = token[(dot + 1)..];

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(encodedPayload));
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(payload, _options.Value.UrlSigningKey);
        if (!FixedTimeEquals(providedSignature, expectedSignature))
        {
            return false;
        }

        var separator = payload.LastIndexOf('|');
        if (separator <= 0)
        {
            return false;
        }

        var key = payload[..separator];
        if (!long.TryParse(
                payload[(separator + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiryUnix))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow().ToUnixTimeSeconds() > expiryUnix)
        {
            return false;
        }

        storageKey = key;
        return true;
    }

    private static string FormatPayload(string storageKey, long expiryUnix) =>
        $"{storageKey}|{expiryUnix.ToString(CultureInfo.InvariantCulture)}";

    private static string ComputeSignature(string payload, string signingKey)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(payload));
        return Base64UrlEncode(hash);
    }

    // Compares two base64url ASCII strings in constant time. FixedTimeEquals returns false for differing
    // lengths, which is the correct answer for a tampered signature.
    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static long CeilingToMultiple(long value, long multiple) =>
        ((value + multiple - 1) / multiple) * multiple;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }
}
