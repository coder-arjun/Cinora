using System.Security.Cryptography;
using System.Text;
using Cinora.Domain.Common;

namespace Cinora.Domain.Entities;

/// <summary>
/// A Web Push subscription for one of a user's devices (Phase 6). It stores the push endpoint and
/// the encryption keys required to send push messages, plus when the subscription was last seen.
/// </summary>
public sealed class Device
{
    private Device()
    {
    }

    /// <summary>The unique identifier of the device subscription.</summary>
    public Guid Id { get; private set; }

    /// <summary>The identifier of the owning user.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The push service endpoint to deliver messages to.</summary>
    public string Endpoint { get; private set; } = null!;

    /// <summary>
    /// The SHA-256 hex digest of <see cref="Endpoint"/> (Milestone 6.3). Fixed-width (64 chars) so it is
    /// indexable — the raw <see cref="Endpoint"/> (up to 2048 chars) exceeds SQL Server's 900-byte index key
    /// limit. Backs the unique <c>(UserId, EndpointHash)</c> index that makes the subscribe upsert race-safe.
    /// </summary>
    public string EndpointHash { get; private set; } = null!;

    /// <summary>The client's P-256 ECDH public key used to encrypt push payloads.</summary>
    public string P256dhKey { get; private set; } = null!;

    /// <summary>The client's authentication secret used to encrypt push payloads.</summary>
    public string AuthSecret { get; private set; } = null!;

    /// <summary>The UTC instant the subscription was registered.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The UTC instant the subscription was last seen active.</summary>
    public DateTime LastSeenUtc { get; private set; }

    /// <summary>Registers a new Web Push subscription for a user's device.</summary>
    /// <param name="userId">The owning user's identifier.</param>
    /// <param name="endpoint">The push service endpoint; required.</param>
    /// <param name="p256dhKey">The client's P-256 ECDH public key; required.</param>
    /// <param name="authSecret">The client's authentication secret; required.</param>
    /// <returns>A new <see cref="Device"/> subscription.</returns>
    public static Device Register(Guid userId, string endpoint, string p256dhKey, string authSecret)
    {
        var now = DateTime.UtcNow;
        var normalizedEndpoint = Guard.Required(endpoint, nameof(endpoint));
        return new Device
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Endpoint = normalizedEndpoint,
            EndpointHash = ComputeEndpointHash(normalizedEndpoint),
            P256dhKey = Guard.Required(p256dhKey, nameof(p256dhKey)),
            AuthSecret = Guard.Required(authSecret, nameof(authSecret)),
            CreatedAtUtc = now,
            LastSeenUtc = now,
        };
    }

    /// <summary>
    /// Computes the fixed-width (64-char, lowercase) SHA-256 hex digest of a push endpoint (Milestone 6.3). Pure
    /// and dependency-free (BCL <see cref="SHA256"/>), so the upsert handler can key on <c>(UserId, EndpointHash)</c>
    /// without exposing the endpoint's raw (unindexable) value.
    /// </summary>
    /// <param name="endpoint">The push service endpoint; required.</param>
    /// <returns>The lowercase 64-character SHA-256 hex digest.</returns>
    public static string ComputeEndpointHash(string endpoint)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Guard.Required(endpoint, nameof(endpoint))));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Rotates the encryption keys for an existing subscription in place and marks it seen (Milestone 6.2). A
    /// browser re-subscribe reuses the same <see cref="Endpoint"/> but can mint fresh keys, so the upsert path
    /// refreshes them here rather than inserting a duplicate <see cref="Device"/> for the same endpoint.
    /// </summary>
    /// <param name="p256dhKey">The client's new P-256 ECDH public key; required.</param>
    /// <param name="authSecret">The client's new authentication secret; required.</param>
    public void UpdateSubscription(string p256dhKey, string authSecret)
    {
        P256dhKey = Guard.Required(p256dhKey, nameof(p256dhKey));
        AuthSecret = Guard.Required(authSecret, nameof(authSecret));
        Seen();
    }

    /// <summary>Records that the subscription was seen active now.</summary>
    public void Seen() => LastSeenUtc = DateTime.UtcNow;
}
