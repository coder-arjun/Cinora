using Cinora.Application.Common.Tmdb;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Tmdb;
using Microsoft.Extensions.Options;

namespace Cinora.Infrastructure.Tests.Tmdb;

/// <summary>
/// Verifies <see cref="TmdbImageUrlBuilder"/> composes absolute TMDB image URLs from the configured image
/// base URL, the requested size token, and the raw path — tolerating a missing leading slash and a trailing
/// slash on the base URL — and returns <c>null</c> for a missing path.
/// </summary>
public sealed class TmdbImageUrlBuilderTests
{
    private static TmdbImageUrlBuilder CreateBuilder(string imageBaseUrl = "https://image.tmdb.org/t/p") =>
        new(Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.themoviedb.org/3",
            ImageBaseUrl = imageBaseUrl,
        }));

    [Fact]
    public void Build_composes_base_size_and_path_for_a_leading_slash_path()
    {
        var url = CreateBuilder().Build("/abc.jpg", TmdbImageSize.W342);

        Assert.Equal("https://images.weserv.nl/?url=image.tmdb.org/t/p/w342/abc.jpg", url);
    }

    [Fact]
    public void Build_inserts_a_slash_when_the_path_has_none()
    {
        var url = CreateBuilder().Build("abc.jpg", TmdbImageSize.W500);

        Assert.Equal("https://images.weserv.nl/?url=image.tmdb.org/t/p/w500/abc.jpg", url);
    }

    [Fact]
    public void Build_trims_a_trailing_slash_on_the_configured_base_url()
    {
        var url = CreateBuilder("https://image.tmdb.org/t/p/").Build("/abc.jpg", TmdbImageSize.W185);

        Assert.Equal("https://images.weserv.nl/?url=image.tmdb.org/t/p/w185/abc.jpg", url);
    }

    [Fact]
    public void Build_maps_the_original_size_token()
    {
        var url = CreateBuilder().Build("/hero.jpg", TmdbImageSize.Original);

        Assert.Equal("https://images.weserv.nl/?url=image.tmdb.org/t/p/original/hero.jpg", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_returns_null_for_a_missing_path(string? path)
    {
        Assert.Null(CreateBuilder().Build(path, TmdbImageSize.W342));
    }
}
