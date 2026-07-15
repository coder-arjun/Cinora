using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Cinora.Application;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Infrastructure;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Jobs;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.Controllers;
using Cinora.Web.Diagnostics;
using Cinora.Web.Hubs;
using Cinora.Web.Infrastructure;
using Cinora.Web.Security;
using FluentValidation;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

// Two-stage Serilog init (error-handling-logging skill). The bootstrap logger captures failures that
// happen while the host itself is being built — before configuration is read — writing to the console
// only. Once the host is up, UseSerilog below replaces it with the fully configured logger.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// Replace the bootstrap logger with the configuration-driven logger: sinks + levels come from the
// "Serilog" section of appsettings, ReadFrom.Services picks up any DI-registered sinks/enrichers, and
// FromLogContext carries scoped properties onto every event. The host owns the logger and flushes it on
// shutdown, so no explicit CloseAndFlush is required here.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// MVC + Razor views. Controllers stay thin; authentication uses UserManager/SignInManager directly
// rather than the mediator (solution-structure.md §7).
//
// F6 — Global anti-forgery: AutoValidateAntiforgeryTokenAttribute validates the token on every unsafe
// request (POST/PUT/PATCH/DELETE) unless an action explicitly opts out, so a forgotten
// [ValidateAntiForgeryToken] can no longer open a CSRF hole. Safe methods (GET/HEAD/OPTIONS/TRACE) —
// including the OAuth callback (GET) — are unaffected. Future HTMX writes send the token via the
// RequestVerificationToken request header rather than a form field.
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

// Milestone 1.6a — HTMX anti-forgery. The token store reads the hidden __RequestVerificationToken
// form field FIRST and only falls back to this header, so real <form> posts (including the auth
// forms and the token-extractor tests) are unaffected; this simply lets future non-form HTMX writes
// carry the token via the "RequestVerificationToken" request header set in Scripts/site.ts. The
// shared layout exposes the token as a <meta> tag. (1.6b/security-agent owns CSP + rate limiting.)
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

// Milestone 1.6a — cache-busting asset resolution. Maps logical names (app.css, site.js) to the
// content-hashed URLs in wwwroot/dist/manifest.json; injected into the layout via @inject.
builder.Services.AddSingleton<IAssetManifest, AssetManifest>();

// Milestone 6.4 (§6.1, ADR 0021) — response compression scoped to the STATIC asset bundle only. The esbuild
// site-*.js (~200 KB) + Tailwind app-*.css (~55 KB) + fonts/svg/manifest are the dominant payload and are
// BREACH-safe (JS/CSS/fonts carry no secret and no reflected user input). text/html is DELIBERATELY EXCLUDED:
// a compressed HTML/HTMX partial carries BOTH the rotating anti-forgery token AND reflected user input
// (reviews, display names) — the classic BREACH exposure — so MimeTypes is an explicit static-only allow-list
// (NOT the framework default, whose list includes text/html). EnableForHttps is safe here because ONLY these
// secret-free static types are in scope. UseResponseCompression runs BELOW SecurityHeadersMiddleware in the
// pipeline, so the live CSP/nosniff/Referrer-Policy/Permissions-Policy set still stamps every compressed asset.
// Provider level is left at the framework default (Fastest) so per-request compression stays cheap on the
// single-instance self-hosted profile; the fingerprinted /dist/* assets are cached immutably (below), so a
// client re-fetches — and re-triggers compression — only on a genuine deploy. This is the AddResponseCompression
// BREACH-safe fallback (design §6.1); MapStaticAssets was NOT adopted because Cinora's custom esbuild → /dist +
// IAssetManifest pipeline and the root sw.js/PwaCacheControl make its build-time fingerprinting risky here.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes =
    [
        "text/css",
        "text/javascript",
        "application/javascript",
        "image/svg+xml",
        "application/manifest+json",
        "font/woff",
        "font/woff2",
        "font/ttf",
        "font/otf",
        "application/font-woff",
        "application/font-woff2",
    ];
});

// F1 — Fail-closed authorization: a global fallback policy requires an authenticated user on every
// endpoint that has no authorization metadata of its own, so anonymous access is an explicit
// [AllowAnonymous] opt-out (landing, login/register, OAuth start+callback, denied) rather than an
// accidental default. Endpoints already carrying [Authorize] (e.g. HomeController, logout) keep it.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// F3 — Rate limiting on the auth endpoints (login/register/external-login). The "auth" policy is a fixed
// window partitioned per client IP, so one abusive caller cannot exhaust everyone else's budget. Rejected
// requests return 429 (not the default 503) BEFORE the action — and its anti-forgery check — runs.
//
// Test-suite safety (per the milestone brief): the integration tests hit /account/login and /account/register
// repeatedly, so the limit is RELAXED to a very high permit in Development/Testing and only strict in
// Production. The relaxed default keeps all existing tests green without disabling the middleware; the permit
// is also overridable via configuration (RateLimiting:AuthPermitLimit) so a focused test can prove real
// throttling with a low value without touching the rest of the suite.
//
// The permit is resolved inside the partitioner from the request-scoped IConfiguration rather than at
// builder time: WebApplicationBuilder freezes builder.Configuration before a test's ConfigureAppConfiguration
// override is applied, but the fully-built IConfiguration (resolved per request) does see it — so a low
// test-only limit takes effect while Production keeps the strict default.
var relaxAuthRateLimit =
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitingPolicies.Auth, httpContext =>
    {
        var permitLimit = httpContext.RequestServices
            .GetRequiredService<IConfiguration>()
            .GetValue<int?>("RateLimiting:AuthPermitLimit")
            ?? (relaxAuthRateLimit ? 10_000 : 10);

        // Partition per client IP so one abusive caller cannot exhaust the shared budget for everyone.
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    // "public-read" — throttles the anonymous, TMDB-proxying browse endpoints (discovery rails + search).
    // Each DISTINCT query is a cache miss forwarded to TMDB, so an unauthenticated flood of unique terms
    // would exhaust Cinora's shared TMDB budget (a search-DoS for real users). Same per-IP fixed-window shape
    // and same relaxed-in-Dev/Testing resolution as "auth" (so the mock-first rail/search integration GETs
    // never trip it); a modest strict Production default, overridable via RateLimiting:PublicReadPermitLimit.
    options.AddPolicy(RateLimitingPolicies.PublicRead, httpContext =>
    {
        // 120/min/IP in Production: a single engaged Discovery session legitimately spends budget fast — the
        // shell + 3 lazy rails + debounced search-as-you-type + infinite-scroll pages — so a tight limit would
        // false-positive a 429 on a real user. 120 leaves ample headroom while still blunting a scripted flood
        // of distinct (cache-missing) queries. Overridable via RateLimiting:PublicReadPermitLimit.
        var permitLimit = httpContext.RequestServices
            .GetRequiredService<IConfiguration>()
            .GetValue<int?>("RateLimiting:PublicReadPermitLimit")
            ?? (relaxAuthRateLimit ? 10_000 : 120);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    // "social-write" — throttles the authenticated social WRITE endpoints (review create/edit/delete in 3.1;
    // likes/comments/friends follow) to blunt write-spam. Partitioned per USER (the authenticated actor), so a
    // spammer can't dodge it by rotating IPs and users behind one NAT don't share a bucket; falls back to IP
    // for the defensive anonymous case (these endpoints are [Authorize], so that's rare). Relaxed in
    // Dev/Testing so the integration suite's repeated writes never trip it; 30/min/user in Production,
    // overridable via RateLimiting:SocialWritePermitLimit.
    options.AddPolicy(RateLimitingPolicies.SocialWrite, httpContext =>
    {
        var permitLimit = httpContext.RequestServices
            .GetRequiredService<IConfiguration>()
            .GetValue<int?>("RateLimiting:SocialWritePermitLimit")
            ?? (relaxAuthRateLimit ? 10_000 : 30);

        var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

// Persistence (DbContext + IAppDbContext), GoogleAuthOptions binding, and the atomic registration
// seam (Milestones 1.2 + 1.3). Called before AddIdentity so CinoraDbContext is registered for the stores.
builder.Services.AddInfrastructure(builder.Configuration);

// Application request pipeline (Milestone 1.4): the hand-rolled mediator (ISender), all request handlers
// and validators in the Application assembly, and the Logging → Validation → Performance behaviors.
builder.Services.AddApplication();

// Milestone 5.3 — Hangfire OSS precompute scheduler (ADR 0018, redeeming ADR 0007's Hangfire deferral). Free
// Hangfire OSS (LGPLv3, no Docker — CLAUDE.md's approved substitution) + SQL Server storage on the SAME database
// AddInfrastructure uses (its own auto-created "HangFire" schema; MARS is already enabled in the connection
// string). The recommendation precompute runs OFF the request path, so the LLM is never called synchronously in
// a request. Reuse the same DefaultConnection key + fail-fast guard AddInfrastructure used above.
var hangfireConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");
var isTesting = builder.Environment.IsEnvironment("Testing");

// Persist the Data-Protection key ring to the DATABASE (dbo.DataProtectionKeys) so the auth cookie +
// anti-forgery tokens survive app restarts AND redeploys. Previously the key ring lived on the filesystem
// (App_Data/keys); the free host wipes the local filesystem on redeploy, so those keys would regenerate and
// silently log EVERY user out — the reported "logged out again and again". DB persistence is durable and
// instance-shared (adopted from DailyPilot/ExpenseTracker). SetApplicationName pins the key isolation and MUST
// stay "Cinora" forever — changing it invalidates all existing cookies. SKIPPED under Testing so parallel
// WebApplicationFactory hosts keep ephemeral keys and never touch the CinoraTest key table.
if (!isTesting)
{
    builder.Services.AddDataProtection()
        .PersistKeysToDbContext<CinoraDbContext>()
        .SetApplicationName("Cinora");
}

builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
    {
        // WHY !isTesting: the integration-test host must NOT create Hangfire's schema on the EF-owned CinoraTest
        // database. The job tests call RunAsync directly (bypassing Hangfire's scheduler/storage entirely), and
        // the anonymous-/jobs test is decided by the dashboard authorization filter BEFORE any storage access —
        // so with schema-prep off (and no connection opened at storage construction) the Testing host still boots.
        PrepareSchemaIfNecessary = !isTesting,
    }));

// The polling BackgroundJobServer is an IHostedService — it must NOT run under the test host (it would open
// connections against the un-prepared Hangfire schema on a background thread and process recurring jobs).
// Non-Testing only; Development/Production run the real job processor.
if (!isTesting)
{
    builder.Services.AddHangfireServer();
}

// Milestone 3.5 — self-hosted SignalR realtime (ADR 0011). AddSignalR() only: NO Azure SignalR, NO Redis/Azure
// backplane, NO Docker (free/local, single instance). The IRealtimeNotifier port's adapter is the SignalR
// SignalRRealtimeNotifier — it lives here in the composition root (Web), not Infrastructure, because it wraps
// the hub/IHubContext (Web transport types) and Infrastructure cannot reference Web (the dependency rule). The
// notification-producing handlers push best-effort behind this port after their co-persisting SaveChanges; with
// no connected clients a group send is a harmless no-op. The hub is mapped below (MapHub, after routing/auth).
builder.Services.AddSignalR();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

// In-app chat realtime (§4, ADR 0024): the IChatNotifier port's adapter is the SignalR SignalRChatNotifier
// (best-effort, same composition-root rationale as above); IChatMembership is the thin scoped membership check
// the ChatHub uses so the hub stays a transport-only dispatcher. The ChatHub is mapped below at /hubs/chat.
builder.Services.AddScoped<IChatNotifier, SignalRChatNotifier>();
builder.Services.AddScoped<IChatMembership, ChatMembership>();

// Global exception handling: AddProblemDetails supplies the RFC 7807 writer; GlobalExceptionHandler
// translates exceptions to ProblemDetails and is active in every environment (see UseExceptionHandler
// below), so clients — and the integration tests — always get ProblemDetails, never a developer page.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Health checks (Milestone 1.7): a single database-connectivity probe backing the /health endpoint. It uses
// EF Core's CanConnectAsync on CinoraDbContext, so no dedicated health-check NuGet package is needed.
// MapHealthChecks("/health") below exposes it anonymously — 200 when the database is reachable, 503 when not.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// Diagnostics endpoints exist only in Development and Testing. Their sample mediator request/validator are
// registered here (the Application assembly scan does not see the Web assembly) and gated on environment so
// the request type cannot even be dispatched elsewhere; the controller actions 404 there as well. Guarding on
// IsDevelopment() || Testing (rather than !IsProduction()) keeps a public Staging environment locked down too.
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddTransient<IRequestHandler<PingCommand, string>, PingCommandHandler>();
    builder.Services.AddScoped<IValidator<PingCommand>, PingCommandValidator>();
}

// ASP.NET Identity with EF Core stores (ADR 0003). Strong password policy + lockout enabled; unique
// email required; email confirmation not required this milestone.
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 1;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<CinoraDbContext>()
    .AddDefaultTokenProviders();

// Application cookie policy. SameSite=Lax is required so the cookie survives the cross-site Google
// OAuth callback redirect (Strict would drop it). HttpOnly + Secure-only + sliding expiration.
// SECURITY (Milestone 5.3, security L3): this SameSite=Lax auth cookie is ALSO the CSRF defense for the Hangfire
// /jobs dashboard's state-changing POSTs (trigger/requeue/delete). Those POSTs are terminal Hangfire middleware,
// NOT MVC endpoints, so the global AutoValidateAntiforgeryToken filter does not cover them. Lax means a cross-site
// POST omits this cookie → the request is anonymous → the dashboard's fail-closed AdminDashboardAuthorizationFilter
// returns 401. This cookie policy MUST NEVER be relaxed to SameSite=None, which would forward the cookie on
// cross-site POSTs and re-open that CSRF vector (see the MapHangfireDashboard site below).
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/denied";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;

    // Force EVERY sign-in to be persistent (30 days), even without a "Remember me" tick. Otherwise a
    // non-persistent login is a SESSION cookie the mobile browser / installed PWA drops as soon as it is
    // closed or evicted — so the user is logged out repeatedly and must sign in again and again. A persistent
    // cookie (with an explicit 30-day expiry) keeps them signed in like WhatsApp/Instagram (adopted from
    // DailyPilot). Fires for password, external (Google), and post-registration sign-ins alike.
    options.Events ??= new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationEvents();
    options.Events.OnSigningIn = context =>
    {
        context.Properties.IsPersistent = true;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
        return Task.CompletedTask;
    };
});

// Conditional Google OAuth: wired only when both client id and secret are supplied (user-secrets in
// development). This lets the app boot and email/password login work WITHOUT Google credentials.
var googleAuth = builder.Configuration
    .GetSection(GoogleAuthOptions.SectionName)
    .Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();

if (googleAuth.IsConfigured)
{
    builder.Services
        .AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleAuth.ClientId;
            options.ClientSecret = googleAuth.ClientSecret;

            // Sign in against Identity's external cookie; the callback links/creates the local user.
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.SaveTokens = false;

            // F5 — Map the email_verified flag: Google returns it in the userinfo payload but the
            // default Google claim actions do not surface it. Mapping it lets the callback decide
            // whether to trust the external email (EmailConfirmed). Google sends a JSON boolean, which
            // maps to the claim string "True"; the callback compares case-insensitively.
            options.ClaimActions.MapJsonKey("email_verified", "email_verified");

            // The correlation cookie must survive the redirect to/from Google (SameSite=Lax) and stay
            // Secure + HttpOnly, mirroring the application cookie.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.CorrelationCookie.HttpOnly = true;
        });
}

var app = builder.Build();

// Milestone 4.2 hardening — MaxUploadBytes ↔ RequestSizeLimit drift guard. SettingsController's coarse
// [RequestSizeLimit(AvatarRequestSizeLimitBytes)] request-body backstop must never be TIGHTER than the precise
// per-file cap FileStorageOptions.MaxUploadBytes; otherwise an operator who raises MaxUploadBytes above the const
// would have valid uploads 413 at the request-size guard BEFORE the streaming cap ever runs (silent config
// drift). The const lives in Web (an attribute needs a compile-time constant) and the option in Infrastructure,
// so the composition root is the one place that sees BOTH — assert the invariant here and fail fast at boot for
// every environment. The shipped defaults (5 MB <= 6 MiB) satisfy it, so the integration host never false-trips.
var configuredMaxUploadBytes =
    app.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.MaxUploadBytes;
if (configuredMaxUploadBytes > SettingsController.AvatarRequestSizeLimitBytes)
{
    throw new InvalidOperationException(
        $"'{FileStorageOptions.SectionName}:{nameof(FileStorageOptions.MaxUploadBytes)}' " +
        $"({configuredMaxUploadBytes} bytes) must not exceed the coarse avatar request-size backstop " +
        $"({SettingsController.AvatarRequestSizeLimitBytes} bytes). A precise per-file cap larger than the " +
        "backstop would 413 valid uploads at the [RequestSizeLimit] guard before the streaming cap runs. Raise " +
        "SettingsController.AvatarRequestSizeLimitBytes (with framing headroom) to stay above MaxUploadBytes.");
}

// Production-environment guard (Milestone 1.7). Diagnostics gating, the developer-only relaxations, and HSTS
// all key off the EXACT environment names Development / Testing / Production. A typo such as
// ASPNETCORE_ENVIRONMENT=Prod is treated as an unknown, non-Production custom environment — which would, for
// example, leave the diagnostics endpoints exposed. This catches that class of mistake at startup with a
// non-fatal Warning (log only; it never blocks boot) so an operator sees it in the logs.
string[] knownEnvironments = ["Development", "Testing", "Production"];
if (!knownEnvironments.Contains(app.Environment.EnvironmentName, StringComparer.Ordinal))
{
    // Logged through the same static Serilog logger used for the bootstrap phase (a structured message
    // template, not interpolation). CA1848's LoggerMessage guidance targets Microsoft.Extensions.Logging;
    // this one-shot startup event on the Serilog API is intentionally kept simple.
    Log.Warning(
        "Unrecognized hosting environment {EnvironmentName}. Expected exactly one of Development, Testing, or " +
        "Production; environment-specific behaviour (diagnostics gating, HSTS) may not apply as intended — " +
        "check ASPNETCORE_ENVIRONMENT for a typo such as 'Prod' instead of 'Production'.",
        app.Environment.EnvironmentName);
}

// Milestone 6.6 (ADR 0021 §deploy) — Web Push is additive and DEGRADES when unconfigured (never a fatal boot
// error — mirrors the conditional Google-OAuth wiring). Surface the disabled state ONCE at startup so a
// deployer isn't puzzled by absent push: with no VAPID keys, WebPushSender no-ops, GET /push/public-key 404s,
// and the subscribe UI stays hidden. Same simple one-shot static-Serilog event as the environment guard above.
if (!app.Services.GetRequiredService<IOptions<WebPushOptions>>().Value.IsConfigured)
{
    Log.Warning(
        "Web Push is DISABLED — no VAPID keys configured (WebPush:Subject / :PublicKey / :PrivateKey via " +
        "user-secrets or environment variables). In-app notifications still work; out-of-app push is off. " +
        "Generate keys with WebPush's VapidHelper.GenerateVapidKeys() to enable.");
}

// Deployment convenience (ADR 0021 posture) — OPT-IN startup migration, OFF by default. Cinora's standing
// invariant is that the app NEVER auto-migrates: schema changes are applied from the reviewed idempotent SQL
// script (artifacts/migrate.sql) or scripts/db-update.ps1. That invariant HOLDS everywhere unless an operator
// explicitly sets Database:MigrateOnStartup=true. The one scenario it exists for is a free managed host (e.g.
// MonsterASP) whose SQL Server is reachable ONLY from the host, which makes the laptop-side "apply migrate.sql"
// path impossible. When enabled (and NEVER under Testing, whose fixtures own their own schema) the app applies
// any pending EF Core migrations once here — before it starts serving — then continues. EF Migrate() is
// idempotent, so a schema that is already current is a no-op. A failure is FATAL and logged: serving requests
// against an un-migrated schema would fail anyway, so failing fast at boot is the honest posture.
if (!isTesting && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var migrationScope = app.Services.CreateScope();
    var startupDbContext = migrationScope.ServiceProvider.GetRequiredService<CinoraDbContext>();
    try
    {
        Log.Information(
            "Database:MigrateOnStartup is enabled — applying any pending EF Core migrations to the target database.");
        startupDbContext.Database.Migrate();
        Log.Information("Startup migration complete — the database schema is up to date.");
    }
    catch (Exception exception)
    {
        Log.Fatal(exception, "Startup migration FAILED; refusing to start with an un-migrated schema.");
        throw;
    }
}

// OUTERMOST middleware: it must wrap everything downstream — including UseExceptionHandler — so it records
// the FINAL, translated status code. If it sat INSIDE the exception handler, a mapped 4xx (ValidationException
// → 400, NotFoundException → 404, ForbiddenAccessException → 403, DomainException → 400) would reach it as an exception still propagating up
// and be logged at Error with StatusCode 500 and a stack trace, BEFORE the handler translated it. As the
// outermost middleware the request returns normally with the handler's final status, so it emits one enriched
// event (method, path, status, elapsed) at the correct level (Information for a 400/404, Error only for a 500).
app.UseSerilogRequestLogging();

// Wrapped by request logging above so the translated status is what gets logged. Catches exceptions from
// everything downstream and returns ProblemDetails. Registered unconditionally (no developer exception page)
// — the global handler owns error translation in all environments, including Testing.
app.UseExceptionHandler();

// F2 — Security headers on every downstream response (CSP, nosniff, Referrer-Policy, Permissions-Policy).
// Placed after the outermost request logging/exception handling and before static files + routing so it
// covers the app's own hashed /dist/ assets, HTMX partials and redirects. (Responses the exception handler
// regenerates after resetting the response are the documented exception — JSON API errors, CSP not relevant.)
app.UseSecurityHeaders();

// HSTS instructs browsers to use HTTPS for future requests; only meaningful over real TLS, so it is
// disabled in Development (and skipped for localhost/loopback by the framework regardless). HTTPS
// redirection upgrades any plain-HTTP request before it reaches static files or the auth cookie.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Milestone 6.4 (§6.1, ADR 0021) — compress the static bundle. SecurityHeadersMiddleware ran UPSTREAM (above),
// so the live CSP/nosniff/Referrer-Policy/Permissions-Policy set already stamped the response before compression
// touches the body; text/html is out of scope (BREACH-safe — see AddResponseCompression). Placed BEFORE
// UseStaticFiles so the /dist/* CSS+JS it serves next are content-negotiated + compressed with a
// Vary: Accept-Encoding. Not a CSP change (SecurityHeadersMiddleware is untouched).
app.UseResponseCompression();

// Milestone 6.1 (ADR 0019) + 6.4 (§6.1) — static-file serving tuned for the PWA. The service worker (/sw.js)
// and the web app manifest (/manifest.webmanifest) are served with Cache-Control: no-cache so the browser
// revalidates them on every load and detects a new deploy promptly. The content-hashed /dist/* assets they
// point at are IMMUTABLE (a byte change is a new hashed URL), so they get long-lived immutable Cache-Control
// (6.4) — separate from the no-cache SW/manifest above. The .webmanifest extension is mapped to
// application/manifest+json. These are cache/content-type tweaks only — NOT a CSP change (SecurityHeadersMiddleware
// is untouched).
var staticContentTypeProvider = new FileExtensionContentTypeProvider();
staticContentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";

// Only fingerprint /dist as immutable OUTSIDE Development: the dev watch build emits FIXED /dist names
// (/dist/app.css, /dist/site.js), so an immutable header would make F5 serve stale assets after an edit
// (only prod names are content-hashed and truly immutable) — gate it so hot reload keeps revalidating.
var immutableDistAssets = !app.Environment.IsDevelopment();

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticContentTypeProvider,
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path;
        if (path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
        else if (path.StartsWithSegments("/dist", StringComparison.OrdinalIgnoreCase))
        {
            // Milestone 6.4 (§6.1) — content-hashed in Release → cache forever (a rebuild changes the hash/URL);
            // in Development the fixed /dist names must revalidate so hot reload isn't masked.
            ctx.Context.Response.Headers.CacheControl = immutableDistAssets
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        }
    },
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Milestone 6.1 (ADR 0019 §3) — the deliberate PWA cache policy on HTML responses: no-store on authenticated
// pages (the SW never caches one user's personalized page on a shared device) and no-cache on anonymous public
// GET pages (so the SW may cache them for offline browsing, relaxing the anti-forgery blanket no-store to a
// revalidate-only directive — never no-store). Placed AFTER UseAuthentication so it can read HttpContext.User;
// static files were served upstream, so their caching is unaffected. NOT a CSP change.
app.UsePwaCacheControl();

// Rate limiting (F3 + Milestone 3.1). Must sit after UseRouting (so the endpoint's [EnableRateLimiting]
// policy is known) and — critically — AFTER UseAuthentication, so a per-USER partition (the "social-write"
// policy) can read the authenticated principal's NameIdentifier claim. Placed before authentication it would
// always see the anonymous principal and silently degrade the per-user bucket to per-IP. The IP-partitioned
// "auth"/"public-read" policies are unaffected by this ordering (RemoteIpAddress is available either way);
// rejected requests still short-circuit before the endpoint runs.
app.UseRateLimiter();

// /health (Milestone 1.7): anonymous liveness + database-connectivity probe for local dev, monitors, and
// load balancers. The global fallback authorization policy is fail-closed, so .AllowAnonymous() is REQUIRED
// for an unauthenticated caller to reach it (REVIEW_BACKLOG global auth contract). It returns 200 "Healthy"
// when the database is reachable and 503 "Unhealthy" when it is not. The endpoint carries no
// [EnableRateLimiting("auth")] metadata, so the auth rate-limiter never throttles it.
app.MapHealthChecks("/health").AllowAnonymous();

// Milestone 3.5 — the self-hosted SignalR notification hub (ADR 0011). Mapped after UseAuthentication/
// UseAuthorization so the hub's [Authorize] is enforced: an anonymous negotiate/connection is rejected
// (fail-closed, N3) and Context.UserIdentifier is the same NameIdentifier Guid ICurrentUser resolves. The
// connection is same-origin (wss://<host>/hubs/notifications), which the strict connect-src 'self' CSP already
// admits — NO CSP change (N6).
app.MapHub<NotificationHub>("/hubs/notifications");

// In-app chat hub (§4, ADR 0024): same-origin (wss://<host>/hubs/chat), already admitted by connect-src 'self'
// — NO CSP change. [Authorize] fail-closed; participation-checked group joins.
app.MapHub<ChatHub>("/hubs/chat");

// Milestone 5.3 — the admin-gated Hangfire /jobs dashboard (ADR 0018 §5, security-hardening). The Hangfire
// dashboard is terminal middleware, NOT an MVC endpoint, so it does not inherit the global fail-closed fallback
// authorization policy. .AllowAnonymous() keeps ASP.NET Core's fallback from 302-redirecting anonymous callers
// to the login page, leaving the dashboard's OWN IDashboardAuthorizationFilter as the sole gate — it returns 401
// on deny (fail-closed) for anonymous AND non-admin callers alike. The filter is built from the resolved
// AdminDashboardOptions admin allow-list + the environment: with no admins configured (the out-of-the-box
// Production default) EVERY caller is denied. Placed after UseAuthentication so the principal is populated.
var adminDashboard = app.Services.GetRequiredService<IOptions<AdminDashboardOptions>>().Value;
var dashboardAuthorization = new AdminDashboardAuthorizationFilter(
    adminDashboard.AdminAccounts.ToHashSet(StringComparer.OrdinalIgnoreCase),
    app.Environment.IsDevelopment(),
    adminDashboard.AllowAnyAuthenticatedInDevelopment);

// SECURITY (Milestone 5.3, security L3): the dashboard's state-changing POSTs (trigger/requeue/delete a job) are
// terminal Hangfire middleware, NOT MVC endpoints, so they are NOT covered by the global anti-forgery filter. Their
// CSRF defense is the SameSite=Lax auth cookie configured above: a cross-site POST omits the cookie → the request
// is anonymous → this fail-closed filter returns 401. Do NOT relax the auth cookie to SameSite=None (it would
// re-open the CSRF vector), and do NOT front the dashboard with an auth scheme that ignores SameSite.
app.MapHangfireDashboard("/jobs", new DashboardOptions
{
    Authorization = new[] { dashboardAuthorization },
}).AllowAnonymous();

// The nightly precompute (Cron.Daily(4) UTC). Gated off in Testing: RecurringJob.AddOrUpdate WRITES the schedule
// to Hangfire storage, which the Testing host deliberately does not prepare. The job is the orchestrator — it
// dispatches GenerateRecommendationsCommand per active user via ISender (ADR 0008), passing an explicit userId
// and never touching the HTTP-bound ICurrentUser.
if (!isTesting)
{
    RecurringJob.AddOrUpdate<RecommendationPrecomputeJob>(
        "recommendations-precompute",
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily(4));
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can bootstrap the app in integration tests
/// (top-level statements otherwise produce an internal <c>Program</c> class).
/// </summary>
public partial class Program;
