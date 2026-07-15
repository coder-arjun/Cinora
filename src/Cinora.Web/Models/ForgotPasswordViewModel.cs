using System.ComponentModel.DataAnnotations;

namespace Cinora.Web.Models;

/// <summary>The bound form model for the "forgot password" request — just the account email.</summary>
public sealed class ForgotPasswordViewModel
{
    /// <summary>The email address to send the password-reset link to.</summary>
    [Required]
    [EmailAddress]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;
}
