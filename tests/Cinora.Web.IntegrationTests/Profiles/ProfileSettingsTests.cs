using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Options;
using Cinora.Web.IntegrationTests.Infrastructure;
using Cinora.Web.IntegrationTests.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cinora.Web.IntegrationTests.Profiles;

/// <summary>
/// Milestone 4.2 mock-first tests for the owner-only settings slice (§4.4 P1–P6), driven end-to-end through the
/// real MVC + hand-rolled <c>ISender</c> + Identity pipeline against the migrated <c>CinoraTest</c> LocalDB. They
/// prove: display-name + privacy update (P1); a valid avatar upload persists the key and renders via the
/// time-limited same-origin token URL, stored under <c>App_Data/uploads</c> not <c>wwwroot</c> (P2); bad-type /
/// magic-byte / oversize rejection (P3); replace best-effort deletes the old file (P4); a tampered serving token
/// from the profile flow is a 404 (P5); and settings require auth (P6, fail-closed). Reuses the shared
/// authenticated-test flow (real Identity cookies) and the <c>AvatarBytes</c> image fixtures.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ProfileSettingsTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ProfileSettingsTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // P1 — POST updates the current user's display name + visibility; a blank/too-long name is a 400.
    [Fact]
    public async Task P1_Update_profile_renames_and_sets_visibility()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Ada Original");

        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var ok = await PostUpdateAsync(user.Client, token, "Ada Renamed", isPublic: false);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var (name, isPublic) = await ProfileRowAsync(user.UserId);
        Assert.Equal("Ada Renamed", name);
        Assert.False(isPublic); // registration defaults to public; the update flipped it to friends-only

        var blankToken = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var blank = await PostUpdateAsync(user.Client, blankToken, "   ", isPublic: true);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var longToken = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var tooLong = await PostUpdateAsync(user.Client, longToken, new string('x', 101), isPublic: true);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // The two rejected posts left the row exactly as the successful update set it.
        var (nameAfter, publicAfter) = await ProfileRowAsync(user.UserId);
        Assert.Equal("Ada Renamed", nameAfter);
        Assert.False(publicAfter);
    }

    // P2 — a valid JPEG upload sets User.AvatarFileKey; the settings page renders an <img> whose src is the
    // same-origin token URL; the file lands under App_Data/uploads, NOT wwwroot.
    [Fact]
    public async Task P2_Avatar_upload_persists_key_and_renders_via_time_limited_url()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Bea Uploader");
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");

        using var response = await PostAvatarAsync(user.Client, token, AvatarBytes.Jpeg(), "me.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The returned _Avatar fragment (not a full page) renders a real <img> served via the token URL.
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<img", fragment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/uploads/avatar?t=", fragment, StringComparison.Ordinal);

        var key = await AvatarKeyAsync(user.UserId);
        Assert.NotNull(key);
        Assert.Matches("^avatars/[0-9a-f]{32}\\.jpg$", key!);

        try
        {
            // The settings page itself now renders the avatar <img> via the same token URL.
            using var page = await user.Client.GetAsync("/settings/profile");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/uploads/avatar?t=", html, StringComparison.Ordinal);

            // The bytes live under App_Data/uploads (outside wwwroot) — never web-servable as a static file.
            var (uploadPath, wwwrootPath) = ResolvePaths(key!);
            Assert.True(File.Exists(uploadPath), $"Expected the avatar under App_Data/uploads at '{uploadPath}'.");
            Assert.False(File.Exists(wwwrootPath), "The avatar must NOT be written under wwwroot.");
        }
        finally
        {
            await CleanupAvatarAsync(key!);
        }
    }

    // P3 — a disallowed declared type, a .jpg-named HTML body (magic-byte gate), and an oversize upload are all
    // rejected (400/413); nothing is persisted.
    [Fact]
    public async Task P3_Avatar_upload_rejects_bad_type_and_oversize()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Cy Rejects");

        // (a) declared SVG → the validator rejects the declared content-type → 400.
        var t1 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var svg = await PostAvatarAsync(
            user.Client, t1, Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>"),
            "x.svg", "image/svg+xml");
        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);

        // (b) a .jpg-named file whose bytes are HTML but declared image/jpeg → passes the declared-type check,
        //     the adapter's magic-byte gate rejects the real bytes → 400.
        var t2 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var fakeJpeg = await PostAvatarAsync(
            user.Client, t2, Encoding.UTF8.GetBytes("<html><body>not an image</body></html>"),
            "evil.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.BadRequest, fakeJpeg.StatusCode);

        // (c) oversize: a valid-JPEG-header file just over MaxUploadBytes → 400 (validator) or 413 (size guard).
        var t3 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        var oversize = new byte[5_000_001];
        var header = AvatarBytes.Jpeg();
        Array.Copy(header, oversize, header.Length); // valid magic bytes at the head
        using var big = await PostAvatarAsync(user.Client, t3, oversize, "big.jpg", "image/jpeg");
        Assert.True(
            big.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"Expected 400 or 413 for an oversize upload, got {(int)big.StatusCode}.");

        // Nothing was written across any of the three rejected uploads.
        Assert.Null(await AvatarKeyAsync(user.UserId));
    }

    // P4 — a second upload updates the key and best-effort deletes the previously stored file (happy path).
    [Fact]
    public async Task P4_Avatar_replace_deletes_the_old_file()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Di Replacer");

        var t1 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var first = await PostAvatarAsync(user.Client, t1, AvatarBytes.Jpeg(), "a.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var key1 = await AvatarKeyAsync(user.UserId);
        Assert.NotNull(key1);
        var (path1, _) = ResolvePaths(key1!);
        Assert.True(File.Exists(path1));

        var t2 = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var second = await PostAvatarAsync(user.Client, t2, AvatarBytes.Png(), "b.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var key2 = await AvatarKeyAsync(user.UserId);
        Assert.NotNull(key2);
        Assert.NotEqual(key1, key2);

        try
        {
            var (path2, _) = ResolvePaths(key2!);
            Assert.True(File.Exists(path2), "The replacement avatar file should exist.");
            Assert.False(File.Exists(path1), "The replaced avatar file should be best-effort deleted.");
        }
        finally
        {
            await CleanupAvatarAsync(key2!);
        }
    }

    // P5 — the token URL minted by the real profile flow serves the bytes (200), but a mutated token is a 404.
    [Fact]
    public async Task P5_Tampered_avatar_token_from_the_profile_flow_is_404()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Ed Tamper");
        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");

        using var upload = await PostAvatarAsync(user.Client, token, AvatarBytes.Webp(), "e.webp", "image/webp");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var key = await AvatarKeyAsync(user.UserId);
        Assert.NotNull(key);

        try
        {
            var url = ExtractAvatarUrl(await upload.Content.ReadAsStringAsync());

            // The serving route is anonymous (the token is the capability): the genuine URL streams the bytes.
            using var anon = TestAuthentication.CreateClient(_factory);
            using var good = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);

            // Flipping the final signature character breaks the HMAC → 404, never a filesystem escape.
            var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');
            using var bad = await anon.GetAsync(tampered);
            Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        }
        finally
        {
            await CleanupAvatarAsync(key!);
        }
    }

    // P6 — anonymous access to every settings endpoint is fail-closed: 302 to /account/login.
    [Fact]
    public async Task P6_Settings_require_auth()
    {
        using var anon = TestAuthentication.CreateClient(_factory);

        using var get = await anon.GetAsync("/settings/profile");
        AssertLoginRedirect(get);

        using var post = await anon.PostAsync(
            "/settings/profile",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["DisplayName"] = "Nope",
                ["IsProfilePublic"] = "true",
            }));
        AssertLoginRedirect(post);

        using var avatarRequest = new HttpRequestMessage(HttpMethod.Post, "/settings/profile/avatar")
        {
            Content = BuildMultipart(AvatarBytes.Jpeg(), "x.jpg", "image/jpeg"),
        };
        using var avatar = await anon.SendAsync(avatarRequest);
        AssertLoginRedirect(avatar);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/settings/profile/avatar");
        using var delete = await anon.SendAsync(deleteRequest);
        AssertLoginRedirect(delete);
    }

    // P7 — the profile update persists the chosen default movie language; an empty choice clears it (Global).
    [Fact]
    public async Task P7_Update_profile_persists_default_language()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Lena Language");

        var token = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var set = await PostUpdateAsync(user.Client, token, user.DisplayName, isPublic: true, defaultLanguage: "hi");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("hi", await DefaultLanguageAsync(user.UserId));

        var clearToken = await TestAuthentication.AntiforgeryTokenAsync(user.Client, "/settings/profile");
        using var clear = await PostUpdateAsync(user.Client, clearToken, user.DisplayName, isPublic: true, defaultLanguage: "");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null(await DefaultLanguageAsync(user.UserId));
    }

    // P8 — renaming to a display name ANOTHER user already holds is rejected (400) and changes nothing (the unique
    // display-name handle that now backs login-by-name must not be stealable via a profile edit).
    [Fact]
    public async Task P8_Rename_to_a_taken_display_name_is_rejected()
    {
        using var taken = await TestAuthentication.RegisterAndSignInAsync(_factory, "Owns The Name");
        using var other = await TestAuthentication.RegisterAndSignInAsync(_factory, "Wants The Name");

        var token = await TestAuthentication.AntiforgeryTokenAsync(other.Client, "/settings/profile");
        using var response = await PostUpdateAsync(other.Client, token, taken.DisplayName, isPublic: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // 'other' still has its own name; the collision changed nothing.
        var (name, _) = await ProfileRowAsync(other.UserId);
        Assert.Equal(other.DisplayName, name);
    }

    // ---- HTTP helpers -------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostUpdateAsync(
        HttpClient client, string token, string displayName, bool isPublic, string? defaultLanguage = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["DisplayName"] = displayName,
            ["IsProfilePublic"] = isPublic ? "true" : "false",
            ["__RequestVerificationToken"] = token,
        };
        if (defaultLanguage is not null)
        {
            fields["DefaultLanguage"] = defaultLanguage;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/settings/profile")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true"); // ask for the _ProfileForm fragment rather than a redirect
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAvatarAsync(
        HttpClient client, string token, byte[] bytes, string fileName, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/settings/profile/avatar")
        {
            Content = BuildMultipart(bytes, fileName, contentType),
        };
        // The multipart avatar POST carries the anti-forgery token via the RequestVerificationToken header.
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent BuildMultipart(byte[] bytes, string fileName, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return content;
    }

    private static string ExtractAvatarUrl(string html)
    {
        var match = Regex.Match(html, "/uploads/avatar\\?t=[^\"'\\s]+");
        Assert.True(match.Success, "Expected a /uploads/avatar?t=... URL in the rendered fragment.");
        return WebUtility.HtmlDecode(match.Value);
    }

    private static void AssertLoginRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.Contains("/account/login", location, StringComparison.OrdinalIgnoreCase);
    }

    // ---- persistence / filesystem helpers -----------------------------------------------------------------

    private async Task<string?> AvatarKeyAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        // IAppDbContext.Users is the DOMAIN User set (CinoraDbContext.Users is the Identity ApplicationUser set).
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.AvatarFileKey)
            .SingleAsync();
    }

    private async Task<string?> DefaultLanguageAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.DefaultLanguage)
            .SingleAsync();
    }

    private async Task<(string DisplayName, bool IsProfilePublic)> ProfileRowAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.DisplayName, user.IsProfilePublic })
            .SingleAsync();
        return (row.DisplayName, row.IsProfilePublic);
    }

    private (string UploadPath, string WwwrootPath) ResolvePaths(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<FileStorageOptions>>().Value;

        var uploadPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.LocalRootPath, key));
        var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var wwwrootPath = Path.GetFullPath(Path.Combine(webRoot, key));
        return (uploadPath, wwwrootPath);
    }

    private async Task CleanupAvatarAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().DeleteAsync(key, CancellationToken.None);
    }
}
