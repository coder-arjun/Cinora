using System.Text.RegularExpressions;

namespace Cinora.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Pulls the ASP.NET Core anti-forgery request token out of a rendered HTML form so a test can POST it
/// back. A fabricated token fails validation with an opaque 400, so the token MUST come from a real GET
/// of the form; the matching anti-forgery cookie is carried automatically by the test
/// <see cref="System.Net.Http.HttpClient"/>'s cookie container (kept across the GET and the POST).
/// </summary>
internal static partial class AntiForgeryTokenExtractor
{
    /// <summary>The hidden field name the MVC form tag helper emits for the request token.</summary>
    private const string FieldName = "__RequestVerificationToken";

    /// <summary>Extracts the anti-forgery request token value from a rendered HTML page.</summary>
    /// <param name="html">The full HTML of a GET response whose form uses the MVC form tag helper.</param>
    /// <returns>The request token to send as the <c>__RequestVerificationToken</c> form field.</returns>
    /// <exception cref="InvalidOperationException">The token hidden field was not present in the markup.</exception>
    public static string Extract(string html)
    {
        var inputMatch = TokenInputRegex().Match(html);
        if (!inputMatch.Success)
        {
            throw new InvalidOperationException(
                $"No '{FieldName}' hidden field was found in the response HTML. " +
                "The form may not use the MVC form tag helper, or anti-forgery generation is disabled.");
        }

        var valueMatch = ValueAttributeRegex().Match(inputMatch.Value);
        if (!valueMatch.Success)
        {
            throw new InvalidOperationException($"The '{FieldName}' field had no value attribute.");
        }

        return valueMatch.Groups[1].Value;
    }

    // Matches the whole <input ... name="__RequestVerificationToken" ...> tag regardless of attribute order.
    [GeneratedRegex("<input\\b[^>]*\\bname=\"__RequestVerificationToken\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TokenInputRegex();

    // Extracts the value="..." attribute from a single input tag.
    [GeneratedRegex("\\bvalue=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttributeRegex();
}
