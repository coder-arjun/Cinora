using System.Security.Claims;
using System.Text;
using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Identity;
using Cinora.Web.Infrastructure;
using Cinora.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace Cinora.Web.Controllers;

/// <summary>
/// Handles local (email/password) and external (Google) authentication. It uses
/// <see cref="SignInManager{TUser}"/>/<see cref="UserManager{TUser}"/> directly — authentication is
/// not routed through MediatR (solution-structure.md §7) — and delegates the atomic two-row account
/// creation to <see cref="IUserRegistrationService"/> (ADR 0003). All state-changing actions validate
/// the anti-forgery token and every external return URL is checked with <c>Url.IsLocalUrl</c> to
/// prevent open redirects.
/// </summary>
[Route("account")]
public sealed class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IUserRegistrationService registrationService,
    ILoginResolver loginResolver,
    IEmailSender emailSender) : Controller
{
    /// <summary>Renders the registration form.</summary>
    /// <param name="returnUrl">An optional local URL to return to after registering.</param>
    /// <returns>The register view.</returns>
    [AllowAnonymous]
    [HttpGet("register")]
    public IActionResult Register(string? returnUrl = null) =>
        View(new RegisterViewModel { ReturnUrl = returnUrl });

    /// <summary>Creates a local account atomically and signs the new user in.</summary>
    /// <param name="model">The submitted registration form.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A redirect on success, or the redisplayed form with errors.</returns>
    [AllowAnonymous]
    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingPolicies.Auth)]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await registrationService.RegisterAsync(
            model.Email, model.Password, model.DisplayName, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result.Errors);
            return View(model);
        }

        var user = await userManager.FindByIdAsync(result.UserId.ToString());
        if (user is null)
        {
            // CR4: registration committed but the principal could not be reloaded. Guard the
            // otherwise null-forgiven SignInAsync — send the user to sign in rather than throw an NRE.
            return LoginWithError("We couldn't sign you in automatically. Please sign in.", model.ReturnUrl);
        }

        await signInManager.SignInAsync(user, isPersistent: false);

        return RedirectToLocal(model.ReturnUrl);
    }

    /// <summary>Renders the sign-in form together with any configured external providers.</summary>
    /// <param name="returnUrl">An optional local URL to return to after signing in.</param>
    /// <returns>The login view.</returns>
    [AllowAnonymous]
    [HttpGet("login")]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (TempData["AuthError"] is string authError)
        {
            ModelState.AddModelError(string.Empty, authError);
        }

        var model = new LoginViewModel
        {
            ReturnUrl = returnUrl,
            ExternalSchemes = [.. await signInManager.GetExternalAuthenticationSchemesAsync()],
        };

        return View(model);
    }

    /// <summary>Attempts a sign-in by email OR display name plus password.</summary>
    /// <param name="model">The submitted sign-in form.</param>
    /// <param name="cancellationToken">A token to cancel the identifier lookup.</param>
    /// <returns>A redirect on success, or the redisplayed form with a generic error.</returns>
    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingPolicies.Auth)]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        model.ExternalSchemes = [.. await signInManager.GetExternalAuthenticationSchemesAsync()];

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Resolve the typed identifier (email OR display name) to the Identity UserName. A null result means no
        // account matched — fall through to the same generic message so we never reveal which handles exist.
        var userName = await loginResolver.ResolveUserNameAsync(model.EmailOrUserName, cancellationToken);
        if (userName is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(
            userName, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return RedirectToLocal(model.ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "This account is temporarily locked. Please try again later.");
            return View(model);
        }

        // WHY: never reveal whether the email exists — one generic message for every credential failure.
        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
    }

    /// <summary>Renders the "forgot password" form.</summary>
    /// <returns>The forgot-password view.</returns>
    [AllowAnonymous]
    [HttpGet("forgot-password")]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    /// <summary>
    /// Emails a single-use password-reset link when the address matches an account, then always shows the same
    /// neutral confirmation — so the response never reveals whether an email is registered (no user enumeration).
    /// </summary>
    /// <param name="model">The submitted forgot-password form (the account email).</param>
    /// <param name="cancellationToken">A token to cancel the send.</param>
    /// <returns>A redirect to the neutral confirmation page, or the redisplayed form on validation error.</returns>
    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingPolicies.Auth)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is not null)
        {
            // Single-use reset token → Base64Url-encoded so it survives the URL/query round-trip unmangled.
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var resetUrl = Url.Action(
                nameof(ResetPassword), "Account",
                new { email = model.Email, code },
                Request.Scheme, Request.Host.Value);

            if (!string.IsNullOrEmpty(resetUrl))
            {
                await emailSender.SendAsync(
                    model.Email, "Reset your Cinora password", BuildResetEmailHtml(resetUrl), cancellationToken);
            }
        }

        // Neutral outcome regardless of whether the address matched.
        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    /// <summary>Neutral "check your email" confirmation shown after any forgot-password submission.</summary>
    /// <returns>The confirmation view.</returns>
    [AllowAnonymous]
    [HttpGet("forgot-password-confirmation")]
    public IActionResult ForgotPasswordConfirmation() => View();

    /// <summary>Renders the reset form from an emailed link carrying the email + encoded token.</summary>
    /// <param name="email">The account email from the link.</param>
    /// <param name="code">The Base64Url-encoded reset token from the link.</param>
    /// <returns>The reset view, or the login page with an error when the link is malformed.</returns>
    [AllowAnonymous]
    [HttpGet("reset-password")]
    public IActionResult ResetPassword(string? email = null, string? code = null)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code))
        {
            return LoginWithError("That password-reset link is invalid or has expired.", returnUrl: null);
        }

        return View(new ResetPasswordViewModel { Email = email, Code = code });
    }

    /// <summary>Applies the new password when the emailed token validates; neutral about unknown addresses.</summary>
    /// <param name="model">The submitted reset form (email, encoded token, new password).</param>
    /// <returns>A redirect to the success page, or the redisplayed form with errors.</returns>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingPolicies.Auth)]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            // Don't reveal that the address is unknown — behave exactly like success.
            return RedirectToAction(nameof(ResetPasswordConfirmation));
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Code));
        }
        catch (FormatException)
        {
            ModelState.AddModelError(string.Empty, "That password-reset link is invalid or has expired.");
            return View(model);
        }

        var result = await userManager.ResetPasswordAsync(user, token, model.Password);
        if (result.Succeeded)
        {
            return RedirectToAction(nameof(ResetPasswordConfirmation));
        }

        AddErrors([.. result.Errors.Select(error => error.Description)]);
        return View(model);
    }

    /// <summary>Success page shown after a completed password reset.</summary>
    /// <returns>The confirmation view.</returns>
    [AllowAnonymous]
    [HttpGet("reset-password-confirmation")]
    public IActionResult ResetPasswordConfirmation() => View();

    /// <summary>Signs the current user out.</summary>
    /// <returns>A redirect to the public landing page.</returns>
    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(HomeController.Landing), "Home");
    }

    /// <summary>Begins an external (e.g. Google) sign-in by challenging the provider.</summary>
    /// <param name="provider">The external authentication scheme name.</param>
    /// <param name="returnUrl">An optional local URL to return to after signing in.</param>
    /// <returns>A challenge that redirects to the external provider.</returns>
    [AllowAnonymous]
    [HttpPost("external-login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingPolicies.Auth)]
    public IActionResult ExternalLogin(string provider, string? returnUrl = null)
    {
        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    /// <summary>Completes an external sign-in, creating and linking the account on first use.</summary>
    /// <param name="returnUrl">An optional local URL to return to after signing in.</param>
    /// <param name="remoteError">An error reported by the external provider, if any.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A redirect on success, or back to the login page with an error.</returns>
    [AllowAnonymous]
    [HttpGet("external-login-callback")]
    public async Task<IActionResult> ExternalLoginCallback(
        string? returnUrl = null,
        string? remoteError = null,
        CancellationToken cancellationToken = default)
    {
        if (remoteError is not null)
        {
            return LoginWithError("The external provider reported an error. Please try again.", returnUrl);
        }

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return LoginWithError("Could not read the external login information. Please try again.", returnUrl);
        }

        var signInResult = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (signInResult.Succeeded)
        {
            return RedirectToLocal(returnUrl);
        }

        if (signInResult.IsLockedOut)
        {
            return LoginWithError("This account is temporarily locked. Please try again later.", returnUrl);
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return LoginWithError("The external provider did not supply an email address.", returnUrl);
        }

        var displayName = info.Principal.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = email.Split('@')[0];
        }

        // F5: only trust the external email as verified when the provider explicitly asserts it. Google
        // returns email_verified as a JSON boolean, which maps to the claim string "True", so compare
        // case-insensitively. Absent or false => the new account's email stays unconfirmed (fail closed).
        var emailVerified = string.Equals(
            info.Principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);

        var result = await registrationService.RegisterExternalAsync(
            info.LoginProvider, info.ProviderKey, email, displayName, emailVerified, cancellationToken);
        if (!result.Succeeded)
        {
            return LoginWithError("Could not create your account from the external login.", returnUrl);
        }

        var user = await userManager.FindByIdAsync(result.UserId.ToString());
        if (user is null)
        {
            // CR4: guard the otherwise null-forgiven SignInAsync against a missing principal.
            return LoginWithError("We couldn't sign you in automatically. Please sign in.", returnUrl);
        }

        await signInManager.SignInAsync(user, isPersistent: false);

        // F7: clear the transient external-scheme cookie left by the OAuth handshake so it cannot
        // linger alongside the freshly issued application cookie. SignInManager.SignOutAsync() is
        // parameterless (and would also drop the application cookie just set), so sign out only the
        // external scheme via HttpContext.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        return RedirectToLocal(returnUrl);
    }

    /// <summary>Renders the access-denied page.</summary>
    /// <returns>The denied view.</returns>
    [AllowAnonymous]
    [HttpGet("denied")]
    public IActionResult Denied() => View();

    private void AddErrors(IReadOnlyList<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }

    private RedirectToActionResult LoginWithError(string message, string? returnUrl)
    {
        TempData["AuthError"] = message;
        return RedirectToAction(nameof(Login), new { returnUrl });
    }

    // Open-redirect guard: only honour local return URLs; otherwise fall back to the authenticated home.
    private IActionResult RedirectToLocal(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction("Index", "Discovery");

    // Branded HTML for the reset email. Inline styles only (CSP does not apply to email, and many clients strip
    // <head>/<style>). The URL is server-generated (Url.Action) and HTML-encoded for the attribute + visible link.
    private static string BuildResetEmailHtml(string resetUrl)
    {
        var safeUrl = System.Net.WebUtility.HtmlEncode(resetUrl);
        return $"""
            <div style="background:#f4f4f5;padding:24px 12px;font-family:Arial,Helvetica,sans-serif;">
              <div style="max-width:480px;margin:0 auto;background:#0b0d12;border-radius:14px;overflow:hidden;border:1px solid #26262b;">
                <div style="padding:28px 32px 6px;">
                  <span style="font-family:Georgia,'Times New Roman',serif;font-size:26px;font-weight:bold;color:#eabf5c;">Cinora</span>
                </div>
                <div style="padding:6px 32px 32px;color:#e8e5df;font-size:15px;line-height:1.6;">
                  <p style="margin:0 0 16px;">We received a request to reset the password for your Cinora account.</p>
                  <p style="margin:0 0 24px;">Tap the button below to choose a new one. This link can be used once and expires shortly. If you didn't request this, you can safely ignore this email — your password won't change.</p>
                  <p style="margin:0 0 24px;">
                    <a href="{safeUrl}" style="display:inline-block;background:#eabf5c;color:#161616;text-decoration:none;font-weight:bold;font-size:15px;padding:12px 24px;border-radius:9px;">Reset password</a>
                  </p>
                  <p style="margin:0;font-size:12px;color:#9a958c;">If the button doesn't work, copy and paste this link into your browser:<br><span style="color:#c9a24a;word-break:break-all;">{safeUrl}</span></p>
                </div>
              </div>
            </div>
            """;
    }
}
