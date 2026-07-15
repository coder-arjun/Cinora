using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Cinora.Web.TagHelpers;

/// <summary>
/// Renders a TMDB image on an <c>&lt;img&gt;</c> element from a RAW TMDB path plus a requested size. Usage:
/// <c>&lt;img asp-tmdb-poster="@card.PosterPath" asp-tmdb-size="W342" alt="@card.Title"&gt;</c>. The <c>src</c>
/// points DIRECTLY at <c>https://image.tmdb.org/t/p/{size}{path}</c> (built by <see cref="ITmdbImageUrlBuilder"/>
/// from <c>TmdbOptions.ImageBaseUrl</c>) so the browser — which can reach TMDB's CDN — loads it, with no
/// server-side dependency. The CSP already allows <c>https://image.tmdb.org</c> in <c>img-src</c>. When the path
/// is <c>null</c>/blank the helper emits a same-origin placeholder instead of a broken image, and it always adds
/// <c>loading="lazy"</c> + <c>decoding="async"</c>. It never invents an <c>alt</c> — the caller supplies it.
/// </summary>
[HtmlTargetElement("img", Attributes = PosterAttributeName)]
public sealed class TmdbImageTagHelper : TagHelper
{
    private const string PosterAttributeName = "asp-tmdb-poster";
    private const string SizeAttributeName = "asp-tmdb-size";
    private const string PlaceholderPath = "~/img/poster-placeholder.svg";

    private readonly IUrlHelperFactory _urlHelperFactory;
    private readonly ITmdbImageUrlBuilder _imageUrlBuilder;

    /// <summary>Creates the tag helper.</summary>
    /// <param name="urlHelperFactory">Resolves the app-relative placeholder URL from the view context.</param>
    /// <param name="imageUrlBuilder">Builds the absolute <c>image.tmdb.org</c> URL from the raw path + size.</param>
    public TmdbImageTagHelper(IUrlHelperFactory urlHelperFactory, ITmdbImageUrlBuilder imageUrlBuilder)
    {
        _urlHelperFactory = urlHelperFactory;
        _imageUrlBuilder = imageUrlBuilder;
    }

    /// <summary>The RAW TMDB poster/image path (e.g. <c>/abc.jpg</c>), or <c>null</c> to render the placeholder.</summary>
    [HtmlAttributeName(PosterAttributeName)]
    public string? Poster { get; set; }

    /// <summary>The render size to request; defaults to <see cref="TmdbImageSize.W342"/> (rail/search cards).</summary>
    [HtmlAttributeName(SizeAttributeName)]
    public TmdbImageSize Size { get; set; } = TmdbImageSize.W342;

    /// <summary>The ambient view context, used to resolve the app-relative placeholder URL (not a bound attribute).</summary>
    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Direct absolute TMDB URL (browser fetches it). A null/blank path falls back to the same-origin
        // placeholder resolved through the app base, never a broken <img>.
        var absolute = _imageUrlBuilder.Build(Poster, Size);
        var src = absolute ?? _urlHelperFactory.GetUrlHelper(ViewContext).Content(PlaceholderPath);
        output.Attributes.SetAttribute("src", src);

        if (!output.Attributes.ContainsName("loading"))
        {
            output.Attributes.SetAttribute("loading", "lazy");
        }

        if (!output.Attributes.ContainsName("decoding"))
        {
            output.Attributes.SetAttribute("decoding", "async");
        }
    }
}
