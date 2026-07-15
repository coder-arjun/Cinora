using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Web.TagHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.TagHelpers;

public sealed class TmdbImageTagHelperTests
{
    private static (TmdbImageTagHelper Helper, TagHelperOutput Output) Build(
        ITmdbImageUrlBuilder urlBuilder, string? poster)
    {
        // The URL-helper factory is used ONLY for the same-origin placeholder; echo its input back.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.Content(Arg.Any<string>()).Returns(call => (string)call[0]!);
        var factory = Substitute.For<IUrlHelperFactory>();
        factory.GetUrlHelper(Arg.Any<ViewContext>()).Returns(urlHelper);

        var helper = new TmdbImageTagHelper(factory, urlBuilder)
        {
            ViewContext = new ViewContext(),
            Poster = poster,
            Size = TmdbImageSize.W342,
        };

        var output = new TagHelperOutput(
            "img",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        return (helper, output);
    }

    [Fact]
    public void Emits_absolute_tmdb_url_and_not_the_proxy_path()
    {
        var urlBuilder = Substitute.For<ITmdbImageUrlBuilder>();
        urlBuilder.Build("/abc.jpg", TmdbImageSize.W342)
            .Returns("https://image.tmdb.org/t/p/w342/abc.jpg");
        var (helper, output) = Build(urlBuilder, "/abc.jpg");

        helper.Process(new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id"), output);

        var src = output.Attributes["src"].Value?.ToString();
        Assert.Equal("https://image.tmdb.org/t/p/w342/abc.jpg", src);
        Assert.DoesNotContain("/tmdb-img/", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_same_origin_placeholder_when_path_is_null()
    {
        var urlBuilder = Substitute.For<ITmdbImageUrlBuilder>();
        urlBuilder.Build(Arg.Any<string?>(), Arg.Any<TmdbImageSize>()).Returns((string?)null);
        var (helper, output) = Build(urlBuilder, null);

        helper.Process(new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id"), output);

        Assert.Equal("~/img/poster-placeholder.svg", output.Attributes["src"].Value?.ToString());
    }
}
