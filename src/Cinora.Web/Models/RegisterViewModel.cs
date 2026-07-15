using System.ComponentModel.DataAnnotations;
using Cinora.Domain.Entities;

namespace Cinora.Web.Models;

/// <summary>The bound form model for local (email/password) account registration.</summary>
public sealed class RegisterViewModel
{
    /// <summary>The public display name for the new profile.</summary>
    [Required]
    [Display(Name = "Display name")]
    [StringLength(User.DisplayNameMaxLength, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The account email address, also used as the sign-in user name.</summary>
    [Required]
    [EmailAddress]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>The chosen password; must satisfy the Identity password policy.</summary>
    [Required]
    [DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 8)]
    [Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>The password confirmation; must equal <see cref="Password"/>.</summary>
    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>The local URL to return to after a successful registration, if any.</summary>
    public string? ReturnUrl { get; set; }
}
