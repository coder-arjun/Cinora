---
name: security-hardening
description: Use when securing Cinora endpoints or reviewing for vulnerabilities — CSRF protection, security headers/CSP, rate limiting, users editing others' reviews, upload abuse, or where secrets should live per environment.
---

# Security Hardening

## Overview
Defense in depth for an MVC app: every unsafe request is anti-forgery-checked and rate-limited, every response carries restrictive headers, and every mutation verifies the caller owns the resource — validation elsewhere is a bonus, not the boundary.

## Quick Reference
| Task | Approach |
|---|---|
| CSRF | Global `AutoValidateAntiforgeryTokenAttribute`; HTMX sends `RequestVerificationToken` header |
| Headers | CSP + `X-Content-Type-Options: nosniff` + HSTS (prod) |
| Rate limiting | Built-in `AddRateLimiter`; strict policy on login, review posts, AI endpoints |
| "Edit own review" | Resource-based authorization handler, checked server-side |
| Secrets | user-secrets (dev) → env vars/Key Vault (prod); never `appsettings.json` |
| Uploads | Allowlist + size cap + magic bytes (see `.claude/skills/azure-blob-storage/SKILL.md`) |
| Input validation | FluentValidation as defense-in-depth (see `.claude/skills/fluent-validation/SKILL.md`) |

## Pattern
```csharp
builder.Services.AddControllersWithViews(o =>
    // WHY: opt-out beats opt-in — every POST/PUT/DELETE validates the token
    // unless explicitly excepted, so a forgotten attribute can't open a CSRF hole
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // WHY: credential stuffing and review spam are per-endpoint problems
    o.AddFixedWindowLimiter("auth", w =>
        { w.Window = TimeSpan.FromMinutes(1); w.PermitLimit = 10; });
});

// Resource-based: ownership can't be evaluated from claims alone
builder.Services.AddSingleton<IAuthorizationHandler, ReviewOwnerHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanEditReview", p => p.Requirements.Add(new ReviewOwnerRequirement()));
// In the controller: await _authz.AuthorizeAsync(User, review, "CanEditReview")
// — never trust a hidden ReviewId or a hidden UserId field

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "img-src 'self' https://image.tmdb.org data:; " + // TMDB posters are hotlinked
        "script-src 'self'; " +   // WHY: use Alpine's CSP build — standard Alpine needs 'unsafe-eval'
        "style-src 'self' 'unsafe-inline'; " + // Tailwind file is 'self'; Alpine x-show sets inline styles
        "frame-ancestors 'none'";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRateLimiter(); // after UseRouting; add [EnableRateLimiting("auth")] on targets
```

## Secrets
`dotnet user-secrets` in development; environment variables or Azure Key Vault (via `AddAzureKeyVault`) in production. Google, TMDB, OpenAI, Redis, and storage credentials all follow this rule — a connection string in `appsettings.json` is a finding, not a convenience. See `.claude/skills/dotnet-configuration-options/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Ownership checked only in the view (hiding the Edit button) | Enforce with resource-based authorization in the action |
| Disabling anti-forgery for AJAX/HTMX | Send the token in the `RequestVerificationToken` header instead |
| CSP with `'unsafe-eval'` for Alpine | Use the `@alpinejs/csp` build |
| Rate limiting only the login page | Also review/comment posts and AI endpoints |
| Filtering "dangerous" input as XSS defense | Razor output-encodes by default; never use `Html.Raw` on user content |
| Auto-linking OAuth accounts by email | See `.claude/skills/aspnet-identity-google-oauth/SKILL.md` |
