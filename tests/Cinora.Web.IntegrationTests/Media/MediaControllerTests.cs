using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Media;

/// <summary>
/// Verifies Milestone 4.1 avatar serving through the token-gated <c>MediaController</c> (ADR 0013 §2.4): a valid
/// signed URL streams the bytes with the forced true content-type, <c>nosniff</c>, and an <b>unchanged</b> CSP;
/// a tampered or expired token is a <c>404</c>. Serving is anonymous (the token is the capability), so no
/// authenticated client is needed. Runs in the shared, non-parallel collection.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class MediaControllerTests
{
    private readonly CinoraWebApplicationFactory _factory;

    public MediaControllerTests(CinoraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Valid_signed_url_serves_bytes_with_nosniff_forced_type_and_unchanged_csp()
    {
        var bytes = AvatarBytes.Jpeg();
        var (key, url) = await SaveAndSignAsync(_factory, bytes, "image/jpeg");

        try
        {
            using var client = CreateClient(_factory);
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Forced TRUE content-type (from the magic-byte-confirmed extension), never a client-supplied one.
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", SingleHeaderValue(response, "X-Content-Type-Options"));

            // The bytes are streamed from App_Data/uploads through the controller (there is no static
            // /uploads/... file), and match exactly what was saved.
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());

            // CSP is UNCHANGED: byte-identical to the app's baseline landing-page CSP (no widening for uploads).
            using var landing = await client.GetAsync("/");
            var avatarCsp = SingleHeaderValue(response, "Content-Security-Policy");
            Assert.Equal(SingleHeaderValue(landing, "Content-Security-Policy"), avatarCsp);
            Assert.Contains("img-src 'self' https://image.tmdb.org https://images.weserv.nl data:", avatarCsp, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(_factory, key);
        }
    }

    [Fact]
    public async Task Conditional_get_with_matching_etag_is_304()
    {
        var bytes = AvatarBytes.Jpeg();
        var (key, url) = await SaveAndSignAsync(_factory, bytes, "image/jpeg");

        try
        {
            using var client = CreateClient(_factory);

            // First GET: 200 with the cheap WEAK validator (length + last-write-time, Milestone 6.4 §6.3) — not a
            // content hash, so it is deliberately weak; If-None-Match uses weak comparison and still matches it.
            using var first = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var etag = first.Headers.ETag;
            Assert.NotNull(etag);
            Assert.True(etag!.IsWeak);
            Assert.StartsWith("\"", etag.Tag, StringComparison.Ordinal);

            // Second GET carrying that ETag as If-None-Match: the framework short-circuits to 304 BEFORE the file
            // is read (PhysicalFileResult), and returns no body.
            using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
            conditional.Headers.IfNoneMatch.Add(etag);
            using var second = await client.SendAsync(conditional);

            Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
            Assert.Empty(await second.Content.ReadAsByteArrayAsync()); // 304 carries no body
        }
        finally
        {
            await DeleteAsync(_factory, key);
        }
    }

    [Fact]
    public async Task Missing_token_is_404()
    {
        using var client = CreateClient(_factory);

        using var response = await client.GetAsync("/uploads/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_token_is_404()
    {
        var (key, url) = await SaveAndSignAsync(_factory, AvatarBytes.Png(), "image/png");

        try
        {
            // Flip the final signature character — the HMAC no longer verifies.
            var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');

            using var client = CreateClient(_factory);
            using var response = await client.GetAsync(tampered);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await DeleteAsync(_factory, key);
        }
    }

    [Fact]
    public async Task Expired_token_is_404()
    {
        var fakeTime = new MutableTimeProvider(DateTimeOffset.UtcNow);

        // A derived host whose avatar signer runs on a clock we control (the last TimeProvider registration wins).
        using var customFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(fakeTime)));

        var (key, url) = await SaveAndSignAsync(customFactory, AvatarBytes.Webp(), "image/webp");

        try
        {
            // Advance well past the (≤ 24h bucketed) expiry, then request the once-valid URL.
            fakeTime.Advance(TimeSpan.FromDays(2));

            using var client = CreateClient(customFactory);
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await DeleteAsync(customFactory, key);
        }
    }

    private static async Task<(string Key, string Url)> SaveAndSignAsync(
        WebApplicationFactory<Program> factory, byte[] bytes, string contentType)
    {
        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var key = await storage.SaveAsync(new MemoryStream(bytes), contentType, CancellationToken.None);
        var url = storage.GetUrl(key, TimeSpan.FromHours(1));
        return (key, url);
    }

    private static async Task DeleteAsync(WebApplicationFactory<Program> factory, string key)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().DeleteAsync(key, CancellationToken.None);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static string SingleHeaderValue(HttpResponseMessage response, string name)
    {
        Assert.True(
            response.Headers.TryGetValues(name, out var values),
            $"Expected the response header '{name}' to be present.");
        return Assert.Single(values!);
    }
}

/// <summary>Minimal image payloads with valid leading magic bytes for the serving tests.</summary>
internal static class AvatarBytes
{
    public static byte[] Jpeg()
    {
        var bytes = new byte[64];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    public static byte[] Png()
    {
        var bytes = new byte[64];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    public static byte[] Webp()
    {
        var bytes = new byte[64];
        bytes[0] = (byte)'R';
        bytes[1] = (byte)'I';
        bytes[2] = (byte)'F';
        bytes[3] = (byte)'F';
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';
        return bytes;
    }
}
