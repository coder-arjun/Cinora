using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Push;
using Cinora.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapidDetails = WebPush.VapidDetails;
using VendorPushSubscription = WebPush.PushSubscription;
using WebPushClient = WebPush.WebPushClient;
using WebPushException = WebPush.WebPushException;

namespace Cinora.Infrastructure.Push;

/// <summary>
/// The <see cref="IPushSender"/> adapter over the free, MIT WebPush library + self-generated VAPID (ADR 0020
/// §3.2). It maps the Application-owned <see cref="PushSubscription"/>/<see cref="PushPayload"/> to the library's
/// types <b>internally</b> — the vendor type never crosses back over the port — serializes the payload to JSON
/// (the shape the service worker's <c>push</c> handler reads: <c>title</c>/<c>body</c>/<c>url</c>/<c>tag</c>),
/// and sends over RFC 8291/8292. VAPID material comes from bound <see cref="WebPushOptions"/> (never
/// <c>IConfiguration</c>; the private key is a secret and is <b>never logged</b>). It is <b>degrade-if-absent</b>:
/// with no VAPID keys configured it returns <see cref="PushSendResult.TransientFailure"/> without a network call
/// (push is simply off). One <see cref="WebPushClient"/> (and its inner <c>HttpClient</c>) is reused for the app
/// lifetime — registered as a singleton, disposed at shutdown.
/// </summary>
internal sealed partial class WebPushSender : IPushSender, IDisposable
{
    // Serializes the payload as { "title": …, "body": …, "url": …, "tag": … } (camelCase; nulls omitted) — the
    // exact object the service worker's push handler consumes via event.data.json().
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebPushClient _client = new();
    private readonly IOptions<WebPushOptions> _options;
    private readonly ILogger<WebPushSender> _logger;

    /// <summary>Creates the sender over the bound VAPID options and a reusable WebPush client.</summary>
    /// <param name="options">The bound VAPID credentials (subject + public/private key).</param>
    /// <param name="logger">Logs a disabled/degraded send; never logs key material.</param>
    public WebPushSender(IOptions<WebPushOptions> options, ILogger<WebPushSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PushSendResult> SendAsync(
        PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(payload);

        var options = _options.Value;
        if (!options.IsConfigured)
        {
            // Push is not configured — degrade to off (no network call, no throw). Treated as a transient
            // (non-pruning) outcome so a later configuration change lets the same subscription receive pushes.
            LogPushDisabled(_logger);
            return PushSendResult.TransientFailure;
        }

        var vapid = new VapidDetails(options.Subject, options.PublicKey, options.PrivateKey);
        var vendorSubscription = new VendorPushSubscription(
            subscription.Endpoint, subscription.P256dh, subscription.Auth);
        var json = JsonSerializer.Serialize(payload, PayloadJsonOptions);

        try
        {
            await _client.SendNotificationAsync(vendorSubscription, json, vapid, cancellationToken);
            return PushSendResult.Delivered;
        }
        catch (WebPushException exception)
        {
            // 404/410 => the subscription is dead (prune the Device); anything else is transient (retry/skip).
            var result = Classify(exception.StatusCode);
            LogSendFailed(_logger, (int)exception.StatusCode, result.ToString());
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any non-cancellation fault outside the library's WebPushException surface — a transport error, a
            // malformed subscription (bad keys → an argument/format/crypto fault inside the library), etc. — is a
            // transient (non-pruning) outcome, never fatal (6.2 code Low: a bad device can't crash the send).
            LogSendErrored(_logger, exception);
            return PushSendResult.TransientFailure;
        }
    }

    /// <summary>Maps a push-service HTTP status to a send result: 404/410 prune the subscription, anything else is transient.</summary>
    /// <param name="statusCode">The status the push service returned (via <c>WebPushException.StatusCode</c>).</param>
    /// <returns><see cref="PushSendResult.Gone"/> for 404/410; otherwise <see cref="PushSendResult.TransientFailure"/>.</returns>
    internal static PushSendResult Classify(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
            ? PushSendResult.Gone
            : PushSendResult.TransientFailure;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    [LoggerMessage(
        EventId = 6210,
        Level = LogLevel.Debug,
        Message = "Web Push is not configured (no VAPID keys); skipping the send (push disabled).")]
    private static partial void LogPushDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 6211,
        Level = LogLevel.Warning,
        Message = "Web Push send failed with status {StatusCode}; classified as {Result}.")]
    private static partial void LogSendFailed(ILogger logger, int statusCode, string result);

    [LoggerMessage(
        EventId = 6212,
        Level = LogLevel.Warning,
        Message = "Web Push send errored transiently.")]
    private static partial void LogSendErrored(ILogger logger, Exception exception);
}
