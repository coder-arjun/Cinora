using System.Text.RegularExpressions;
using Cinora.Application.Common.Exceptions;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinora.Infrastructure.Tests.Storage;

/// <summary>
/// Verifies the authoritative upload gate and round-trip of <see cref="LocalFileStorage"/> (ADR 0013 §2): valid
/// JPEG/PNG/WebP persist under <c>App_Data/uploads</c> (outside <c>wwwroot</c>) with a server-generated key,
/// mint a same-origin token URL, and delete; bad-type/oversize uploads are rejected with a
/// <see cref="ValidationException"/> and leave no file; a malformed/traversal key never escapes the root.
/// </summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private static readonly Regex KeyPattern =
        new("^avatars/[0-9a-f]{32}\\.(jpg|png|webp)$", RegexOptions.CultureInvariant);

    private readonly TestHostEnvironment _environment = new();

    private LocalFileStorage CreateStorage(long maxBytes = 5_000_000)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FileStorageOptions
        {
            LocalRootPath = "App_Data/uploads",
            PublicBasePath = "/uploads",
            MaxUploadBytes = maxBytes,
            UrlSigningKey = "unit-test-signing-key",
            SignedUrlBucketHours = 24,
        });

        return new LocalFileStorage(
            options,
            _environment,
            new ImageContentInspector(),
            new AvatarUrlSigner(options, TimeProvider.System),
            NullLogger<LocalFileStorage>.Instance);
    }

    public static TheoryData<byte[], string, string> ValidImages() => new()
    {
        { AvatarImageFixtures.Jpeg(), "image/jpeg", ".jpg" },
        { AvatarImageFixtures.Png(), "image/png", ".png" },
        { AvatarImageFixtures.Webp(), "image/webp", ".webp" },
    };

    [Theory]
    [MemberData(nameof(ValidImages))]
    public async Task Save_get_url_delete_round_trips_a_valid_image(byte[] bytes, string contentType, string extension)
    {
        var storage = CreateStorage();

        var key = await storage.SaveAsync(new MemoryStream(bytes), contentType, CancellationToken.None);

        // Server-generated key: avatars/{32-hex}{ext}, extension from the CONFIRMED magic-byte type.
        Assert.Matches(KeyPattern, key);
        Assert.EndsWith(extension, key, StringComparison.Ordinal);

        // The file lands under {contentRoot}/App_Data/uploads — outside wwwroot.
        var expectedPath = Path.Combine(_environment.ContentRootPath, "App_Data", "uploads", key.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(expectedPath), $"Expected the avatar at '{expectedPath}'.");
        Assert.Contains($"App_Data{Path.DirectorySeparatorChar}uploads", expectedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("wwwroot", expectedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedPath));

        // GetUrl is a same-origin, token-bearing URL under the public base path.
        var url = storage.GetUrl(key, TimeSpan.FromHours(1));
        Assert.StartsWith("/uploads/avatar?t=", url, StringComparison.Ordinal);

        // DeleteAsync removes it and is idempotent on a second call.
        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.False(File.Exists(expectedPath));
        await storage.DeleteAsync(key, CancellationToken.None);
    }

    [Fact]
    public async Task Save_rejects_a_plain_text_body()
    {
        var storage = CreateStorage();

        await Assert.ThrowsAsync<ValidationException>(() =>
            storage.SaveAsync(new MemoryStream(AvatarImageFixtures.Text()), "image/jpeg", CancellationToken.None));

        AssertNoFilesWritten();
    }

    [Fact]
    public async Task Save_rejects_an_svg_body_even_when_declared_as_an_image()
    {
        var storage = CreateStorage();

        // SVG is the classic image-as-XSS vector and is deliberately absent from the allow-list.
        await Assert.ThrowsAsync<ValidationException>(() =>
            storage.SaveAsync(new MemoryStream(AvatarImageFixtures.Svg()), "image/webp", CancellationToken.None));

        AssertNoFilesWritten();
    }

    [Fact]
    public async Task Save_rejects_a_body_whose_bytes_are_not_the_declared_image()
    {
        var storage = CreateStorage();

        // Declared image/png, but the bytes are text → the magic-byte gate rejects regardless of the declaration.
        await Assert.ThrowsAsync<ValidationException>(() =>
            storage.SaveAsync(new MemoryStream(AvatarImageFixtures.Text()), "image/png", CancellationToken.None));

        AssertNoFilesWritten();
    }

    [Fact]
    public async Task Save_rejects_an_oversize_upload_and_leaves_no_file()
    {
        var storage = CreateStorage(maxBytes: 64);

        // A valid JPEG whose length (256) exceeds the 64-byte cap → aborted past the cap, no partial left behind.
        await Assert.ThrowsAsync<ValidationException>(() =>
            storage.SaveAsync(new MemoryStream(AvatarImageFixtures.Jpeg(256)), "image/jpeg", CancellationToken.None));

        AssertNoFilesWritten();
    }

    [Fact]
    public async Task Save_rejects_an_empty_file_and_leaves_no_file()
    {
        var storage = CreateStorage();

        // A 0-byte body has no magic bytes to confirm, so it is rejected at the header gate before any file is
        // ever created — nothing to clean up, nothing left on disk.
        await Assert.ThrowsAsync<ValidationException>(() =>
            storage.SaveAsync(new MemoryStream(Array.Empty<byte>()), "image/jpeg", CancellationToken.None));

        AssertNoFilesWritten();
    }

    [Fact]
    public async Task Save_accepts_an_upload_exactly_at_the_size_cap()
    {
        const int cap = 64;
        var storage = CreateStorage(maxBytes: cap);

        // Boundary: a valid JPEG whose length == MaxUploadBytes must SUCCEED — the cap check is inclusive
        // ('total > max', not '>='). A small cap keeps the payload tiny instead of allocating the real 5 MB.
        var key = await storage.SaveAsync(
            new MemoryStream(AvatarImageFixtures.Jpeg(cap)), "image/jpeg", CancellationToken.None);

        Assert.Matches(KeyPattern, key);

        await storage.DeleteAsync(key, CancellationToken.None);
    }

    [Fact]
    public void IsValid_rejects_a_key_with_a_trailing_newline()
    {
        // Regression for the '\z' end anchor (was '$'): in non-Multiline .NET regex '$' also matches just before a
        // single trailing '\n', so a key with a trailing newline must NOT pass the single key authority.
        Assert.False(AvatarStorageKey.IsValid("avatars/0123456789abcdef0123456789abcdef.jpg\n"));
        Assert.True(AvatarStorageKey.IsValid("avatars/0123456789abcdef0123456789abcdef.jpg"));
    }

    [Theory]
    [InlineData("avatars/../../etc/passwd")]
    [InlineData("../secret.txt")]
    [InlineData("avatars/not-a-valid-key.jpg")]
    [InlineData("avatars/0123456789abcdef0123456789abcdef.exe")]
    public async Task Delete_ignores_a_malformed_or_traversal_key(string key)
    {
        var storage = CreateStorage();

        // A malformed / '..'-bearing key is a silent no-op — never a filesystem escape or a throw.
        await storage.DeleteAsync(key, CancellationToken.None);
    }

    [Theory]
    [InlineData("avatars/../../etc/passwd")]
    [InlineData("avatars/not-a-valid-key.jpg")]
    public void GetUrl_throws_for_a_malformed_key(string key)
    {
        var storage = CreateStorage();

        Assert.Throws<ArgumentException>(() => storage.GetUrl(key, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Saved_url_verifies_and_resolves_to_the_true_file()
    {
        var storage = CreateStorage();
        var bytes = AvatarImageFixtures.Png();

        var key = await storage.SaveAsync(new MemoryStream(bytes), "image/png", CancellationToken.None);
        var url = storage.GetUrl(key, TimeSpan.FromHours(1));
        var token = url["/uploads/avatar?t=".Length..];

        // The serving seam verifies the token and RESOLVES (no read) the forced true content-type + the cheap
        // weak validator; the resolved physical path holds the exact bytes that were saved (Milestone 6.4 §6.3).
        var file = storage.TryResolve(token);

        Assert.NotNull(file);
        Assert.Equal("image/png", file!.ContentType);
        Assert.StartsWith("W/\"", file.ETag, StringComparison.Ordinal); // weak length+mtime validator
        Assert.True(File.Exists(file.PhysicalPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file.PhysicalPath));

        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.Null(storage.TryResolve(token));
    }

    private void AssertNoFilesWritten()
    {
        var avatarsDir = Path.Combine(_environment.ContentRootPath, "App_Data", "uploads", "avatars");
        if (Directory.Exists(avatarsDir))
        {
            Assert.Empty(Directory.GetFiles(avatarsDir));
        }
    }

    public void Dispose() => _environment.Dispose();
}
