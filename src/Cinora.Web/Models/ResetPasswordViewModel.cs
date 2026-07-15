using System.ComponentModel.DataAnnotations;

namespace Cinora.Web.Models;

/// <summary>The bound form model for choosing a new password from an emailed reset link.</summary>
public sealed class ResetPasswordViewModel
{
    /// <summary>The account email, carried from the reset link through the form.</summary>
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The Base64Url-encoded password-reset token from the email link, round-tripped through the form and decoded
    /// server-side. Opaque to the user; carried in a hidden field.
    /// </summary>
    [Required]
    public string Code { get; set; } = string.Empty;

    /// <summary>The new password; must satisfy the Identity password policy.</summary>
    [Required]
    [DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 8)]
    [Display(Name = "New password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>The confirmation; must equal <see cref="Password"/>.</summary>
    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
