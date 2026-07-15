---
name: aspnet-identity-google-oauth
description: Use when setting up or debugging authentication in Cinora — ASP.NET Identity with EF Core, Google sign-in, external login callbacks failing, cookie expiration/SameSite behavior, or linking Google accounts to existing users.
---

# ASP.NET Identity + Google OAuth

## Overview
Identity owns local accounts and cookies; Google is an *external* login attached via `AddAuthentication().AddGoogle()`. Keep the cookie scheme as the app's single source of truth — Google only proves identity once at sign-in.

## Quick Reference
| Task | Approach |
|---|---|
| Custom user | `ApplicationUser : IdentityUser<Guid>` (DisplayName, AvatarUrl) + `AddEntityFrameworkStores<CinoraDbContext>()` |
| Google setup | `AddAuthentication().AddGoogle(...)`, secrets from Options/user-secrets |
| External flow | `ChallengeAsync` → callback → `GetExternalLoginInfoAsync` → `ExternalLoginSignInAsync` |
| First-time Google user | Create `ApplicationUser`, then `AddLoginAsync(user, info)` |
| Extra claims | `options.ClaimActions.MapJsonKey("picture", "picture")` |
| Cookie policy | Sliding expiration, `SameSite=Lax`, HttpOnly |

## Pattern
```csharp
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(o =>
{
    o.User.RequireUniqueEmail = true;
    o.SignIn.RequireConfirmedEmail = true; // Google emails arrive pre-verified
})
.AddEntityFrameworkStores<CinoraDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthentication()
    .AddGoogle(o =>
    {
        // WHY: bound Options keep ClientSecret out of source (user-secrets / Key Vault)
        var google = builder.Configuration
            .GetSection(GoogleAuthOptions.SectionName).Get<GoogleAuthOptions>()!;
        o.ClientId = google.ClientId;
        o.ClientSecret = google.ClientSecret;
        o.ClaimActions.MapJsonKey("picture", "picture"); // avatar seed for ApplicationUser
        o.SaveTokens = false; // WHY: Cinora never calls Google APIs after sign-in
    });

builder.Services.ConfigureApplicationCookie(o =>
{
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;                  // WHY: active users stay signed in
    o.Cookie.SameSite = SameSiteMode.Lax;        // WHY: Strict drops the cookie on the
    o.Cookie.HttpOnly = true;                    //      cross-site OAuth callback redirect
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/denied";
});
```

In the callback action: `GetExternalLoginInfoAsync()`, try `ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true)`; on failure, create the user from `info.Principal` claims and call `AddLoginAsync`.

## Account Linking Pitfalls
Never auto-link a Google login to an existing local account just because emails match — an attacker controlling a Google account with a victim's email could take over the account. Require the user to be signed in locally first, then link via `AddLoginAsync`. Only trust matching email when the provider asserts `email_verified: true`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| `SameSite=Strict` on the auth cookie | OAuth callback loses the correlation cookie; use `Lax` |
| Auto-linking external login by email | Account takeover risk — link only while authenticated |
| ClientSecret in `appsettings.json` | user-secrets locally, env vars/Key Vault in prod (see `.claude/skills/security-hardening/SKILL.md`) |
| Forgetting `AddDefaultTokenProviders()` | Password reset / email confirmation tokens fail |
| Reading Google claims after sign-in | Claims exist only in the callback; persist what you need on `ApplicationUser` |

Bind `GoogleAuthOptions` per `.claude/skills/dotnet-configuration-options/SKILL.md`.
