using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Cinora.Web.Models;

/// <summary>The bound form model for local sign-in, plus the external providers offered on the page.</summary>
public sealed class LoginViewModel
{
    /// <summary>The account email address OR display name used to sign in.</summary>
    [Required]
    [Display(Name = "Display name or email")]
    public string EmailOrUserName { get; set; } = string.Empty;

    /// <summary>The account password.</summary>
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether to issue a persistent (remember-me) authentication cookie.</summary>
    [Display(Name = "Remember me")]
    public bool RememberMe { get; set; }

    /// <summary>The local URL to return to after a successful sign-in, if any.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// The external authentication schemes to render (e.g. Google); empty when none are configured.
    /// Populated by the controller and never bound or validated from the request.
    /// </summary>
    [BindNever]
    [ValidateNever]
    public IList<AuthenticationScheme> ExternalSchemes { get; set; } = [];
}
