using System.ClientModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Cinora.Application.Common.Interfaces;
using Cinora.Infrastructure.Ai;
using Cinora.Infrastructure.Email;
using Cinora.Infrastructure.Identity;
using Cinora.Infrastructure.Jobs;
using Cinora.Infrastructure.Options;
using Cinora.Infrastructure.Persistence;
using Cinora.Infrastructure.Push;
using Cinora.Infrastructure.Storage;
using Cinora.Infrastructure.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Cinora.Infrastructure;

/// <summary>Composition-root wiring for the Infrastructure layer.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the Infrastructure services: the <see cref="CinoraDbContext"/> against SQL Server
    /// (LocalDB in development) exposed as the Application-owned <see cref="IAppDbContext"/>, the bound
    /// <see cref="GoogleAuthOptions"/>, the free-provider options (<see cref="TmdbOptions"/> — now
    /// <c>ValidateOnStart</c> since Milestone 2.0 consumes TMDB — plus <see cref="AiOptions"/>,
    /// <see cref="CacheOptions"/>, <see cref="FileStorageOptions"/>, still lazily validated), the atomic
    /// <see cref="IUserRegistrationService"/> seam (Milestone 1.3), and the TMDB integration (the typed
    /// <see cref="ITmdbClient"/> adapter with resilience and the <see cref="ITmdbImageUrlBuilder"/>,
    /// Milestone 2.0). ASP.NET Identity itself (<c>AddIdentity</c>, cookie policy, <c>AddGoogle</c>) is
    /// wired at the Web composition root (ADR 0003), which calls this method first.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="configuration">Application configuration, source of the <c>DefaultConnection</c> string and the <c>GoogleAuth</c> section.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Fail fast at composition with a clear message rather than an opaque error on first DB use.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        // CR6: EnableRetryOnFailure MUST stay OFF here. UserRegistrationService opens a user-initiated
        // BeginTransactionAsync spanning UserManager.CreateAsync + the domain-user insert; a retrying
        // execution strategy throws ("The configured execution strategy 'SqlServerRetryingExecutionStrategy'
        // does not support user-initiated transactions") unless that two-step write is wrapped in
        // dbContext.Database.CreateExecutionStrategy().ExecuteAsync(...). If retries are ever enabled,
        // wrap the registration writes in an execution strategy first.
        services.AddDbContext<CinoraDbContext>(options => options.UseSqlServer(connectionString));

        // The Application layer depends on the abstraction; the same scoped context instance backs it.
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<CinoraDbContext>());

        // Google OAuth options (§6): bound at the composition root. Deliberately NOT ValidateOnStart —
        // email/password login must boot without Google credentials; Program.cs wires AddGoogle only
        // when both credentials are present (GoogleAuthOptions.IsConfigured).
        services.AddOptions<GoogleAuthOptions>()
            .BindConfiguration(GoogleAuthOptions.SectionName);

        // Free-provider options (§6): TMDB (free API), Ollama-backed AI (free local LLM), the cache
        // backend (in-memory by default), and local file storage. Each is bound and validated against its
        // data annotations. ValidateUsingDataAnnotations is the in-house, package-free equivalent of the
        // framework's ValidateDataAnnotations (whose package Infrastructure does not reference).
        //
        // Milestone 2.0: TMDB is now consumed (the typed TmdbClient below), so TmdbOptions is switched to
        // ValidateOnStart — a missing or blank Tmdb:ApiKey fails fast at boot (the REVIEW_BACKLOG
        // fail-closed gate) rather than surfacing on the first movie page view. Testing supplies a dummy
        // non-secret key so the suite still boots. Ai/Cache/FileStorage remain lazily validated (NOT
        // ValidateOnStart) until the phase that first consumes each (Phase 5).
        services.AddOptions<TmdbOptions>()
            .BindConfiguration(TmdbOptions.SectionName)
            .ValidateUsingDataAnnotations()
            .ValidateOnStart();

        // Milestone 5.0 (ADR 0016): the LLM is now consumed — AddAi below registers the first consumer
        // (OllamaRecommendationEngine) — so AiOptions is switched to ValidateOnStart, mirroring Phase 2 flipping
        // TmdbOptions. A missing/blank Endpoint or Model, a non-URL Endpoint, or an out-of-range timeout/output
        // cap now fails fast at boot rather than surfacing on the first recommendation run. The defaults are the
        // local, free Ollama endpoint + model, so Development/Testing boot with no configuration and no API key.
        services.AddOptions<AiOptions>()
            .BindConfiguration(AiOptions.SectionName)
            .ValidateUsingDataAnnotations()
            .ValidateOnStart();

        // Milestone 5.1 (ADR 0017): the recommendation caps are consumed this phase (candidate generation + the
        // hallucination guard), so RecommendationOptions is ValidateOnStart — an out-of-range cap fails fast at
        // boot. The caps reach the Application seams via IRecommendationPolicy (registered in AddAi), never this
        // Options type directly (the dependency rule, mirroring FileStorageOptions/IUploadPolicy).
        services.AddOptions<RecommendationOptions>()
            .BindConfiguration(RecommendationOptions.SectionName)
            .ValidateUsingDataAnnotations()
            .ValidateOnStart();

        // Milestone 5.3 (ADR 0018 §5, security-hardening): the admin allow-list for the Hangfire /jobs dashboard.
        // Validated at boot because the dashboard is a real attack surface. Fail-closed by default — an empty
        // AdminAccounts (the out-of-the-box Production value) denies EVERYONE at the authorization filter. The
        // values reach the Web-layer AdminDashboardAuthorizationFilter at MapHangfireDashboard time (the same
        // Options → Web-composition seam as FileStorageOptions), never an Application dependency.
        services.AddOptions<AdminDashboardOptions>()
            .BindConfiguration(AdminDashboardOptions.SectionName)
            .ValidateUsingDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CacheOptions>()
            .BindConfiguration(CacheOptions.SectionName)
            .ValidateUsingDataAnnotations();

        // Milestone 4.1: FileStorage is now consumed (avatar upload + serving, ADR 0013), so it is switched to
        // ValidateOnStart — mirroring Phase 2 flipping TmdbOptions. PostConfigure auto-generates an ephemeral
        // UrlSigningKey in Development/Testing when none is configured (so local dev + the test suite boot
        // without a real secret); FileStorageProductionValidateOptions then makes a MISSING key in Production a
        // fail-fast boot error rather than an unverifiable-URL runtime hazard.
        services.AddOptions<FileStorageOptions>()
            .BindConfiguration(FileStorageOptions.SectionName)
            .PostConfigure<IHostEnvironment>((options, environment) =>
            {
                if (string.IsNullOrWhiteSpace(options.UrlSigningKey) && !environment.IsProduction())
                {
                    options.UrlSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                }
            })
            .ValidateUsingDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<FileStorageOptions>, FileStorageProductionValidateOptions>();

        // Milestone 4.1 hardening: turn a silent mint↔serve route drift into a loud boot failure. For the Local
        // provider, asserts FileStorageOptions.PublicBasePath still targets the AvatarServingRoute the
        // MediaController serves — otherwise every minted avatar URL would 404 with no error. Runs under the same
        // ValidateOnStart as the Production key check above.
        services.AddSingleton<IValidateOptions<FileStorageOptions>, FileStorageRouteValidateOptions>();

        // Atomic registration seam: creates ApplicationUser + Domain.User in one transaction (ADR 0003;
        // the 1.3 blocking requirement). Scoped so it shares the request's CinoraDbContext with UserManager.
        services.AddScoped<IUserRegistrationService, UserRegistrationService>();

        // Login-by-display-name-or-email resolver: resolves a typed identifier to the Identity UserName for
        // PasswordSignInAsync. Scoped alongside the UserManager and DbContext it queries.
        services.AddScoped<ILoginResolver, LoginResolver>();

        // Current-user seam (Milestone 3.1, ADR 0009): every Phase-3 write handler resolves the acting user
        // from ICurrentUser (server-side, from the request ClaimsPrincipal) — never a client-bound field.
        // AddHttpContextAccessor registers the IHttpContextAccessor the adapter reads (TryAdd — idempotent).
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        // Exact-match user directory (Milestone B2, friends-social-overhaul plan): resolves a user id from an
        // Identity-only attribute (today, email) so the Application-layer search stays free of any Identity type.
        services.AddScoped<IUserDirectory, UserDirectory>();

        // Password-reset email over SMTP (IEmailSender port → SmtpEmailSender/MailKit adapter). Validated lazily
        // for FORMAT-if-present but NOT ValidateOnStart-ed: the mailer is optional (Email:Enabled defaults false),
        // and an absent/blank config degrades the forgot-password flow to a no-op-and-log, never a boot failure.
        // The password is a secret — user-secrets / env var only. Singleton: the adapter is stateless (it opens a
        // fresh SmtpClient per send).
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .ValidateUsingDataAnnotations();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        AddTmdb(services);
        AddFileStorage(services);
        AddAi(services);
        AddPush(services);

        return services;
    }

    /// <summary>
    /// Registers the Web Push slice (Milestone 6.2, ADR 0020): the bound <see cref="WebPushOptions"/> (VAPID
    /// credentials) and the two adapters behind their Application ports. <see cref="WebPushOptions"/> is validated
    /// lazily for FORMAT-if-present but is <b>NOT</b> <c>ValidateOnStart</c>-ed — an absent VAPID configuration
    /// degrades push to off, it does not crash boot (design §8, degrade-if-absent). The private VAPID key is a
    /// secret and must come from user-secrets / environment, never appsettings.
    /// <para>
    /// Both adapters are singletons: <see cref="WebPushSender"/> owns and reuses one WebPush client (and its inner
    /// <c>HttpClient</c>) for the app lifetime — disposed by the container at shutdown — and <see cref="InlinePushDispatch"/>
    /// is a stateless dispatcher that creates its own fresh scope per send via the injected
    /// <see cref="IServiceScopeFactory"/>; neither captures a scoped dependency, so a singleton is correct and
    /// avoids per-send <c>HttpClient</c> churn.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    private static void AddPush(IServiceCollection services)
    {
        services.AddOptions<WebPushOptions>()
            .BindConfiguration(WebPushOptions.SectionName)
            .ValidateUsingDataAnnotations();

        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddSingleton<IPushDispatch, InlinePushDispatch>();
    }

    /// <summary>
    /// Registers the free, local AI recommendation engine (Milestone 5.0, ADR 0016): a Microsoft.Extensions.AI
    /// <see cref="IChatClient"/> built from <see cref="AiOptions"/> over the OpenAI-compatible protocol Ollama
    /// exposes at its local <c>/v1</c> endpoint, and the <see cref="OllamaRecommendationEngine"/> behind the
    /// Application <see cref="IRecommendationEngine"/> port. Endpoint and model come from <see cref="AiOptions"/>
    /// and are NEVER hardcoded (and never the paid <c>api.openai.com</c>); a free-tier cloud model (Gemini/Groq)
    /// is selected by changing those options plus a real free key, with no code change (ADR 0016). The vendor
    /// types stay inside the adapter — handlers depend only on the port.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    private static void AddAi(IServiceCollection services)
    {
        // The IChatClient is the provider-agnostic seam (Microsoft.Extensions.AI). It is built lazily from
        // AiOptions on first resolution — NOT at boot — so the app starts even when Ollama is not running (the
        // request path never calls the model; the precompute job does, Milestone 5.3). The credential comes from
        // AiOptions.ApiKey: local Ollama ignores it (default placeholder "ollama"), while a free-tier cloud
        // provider (Groq, or Gemini's OpenAI-compatible endpoint) is enabled by supplying a real key plus the
        // matching Endpoint + Model — with no code change (ADR 0016).
        services.AddChatClient(serviceProvider =>
        {
            var ai = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
            var client = new OpenAIClient(
                new ApiKeyCredential(ai.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(ai.Endpoint) });
            return client.GetChatClient(ai.Model).AsIChatClient();
        });

        // Scoped like the other request-lifetime services: the engine reads AiOptions, builds the prompt, calls
        // the IChatClient, and parses/validates the JSON. The vendor types never leave this layer.
        services.AddScoped<IRecommendationEngine, OllamaRecommendationEngine>();

        // Surfaces the recommendation caps (RecommendationOptions) to the Application seams via the
        // IRecommendationPolicy port (Milestone 5.1, ADR 0017) — the same dependency-rule seam as
        // IUploadPolicy/UploadPolicy. Stateless singleton over the bound options.
        services.AddSingleton<IRecommendationPolicy, RecommendationPolicy>();

        // The served-recommendation cache (Milestone 5.2, ADR 0018 §2): the per-user serve store the
        // GenerateRecommendationsCommand writer warms and the GetMyRecommendationsQuery reader consumes, over the
        // free in-memory IDistributedCache (registered by AddTmdb's AddDistributedMemoryCache). Stateless over the
        // singleton IDistributedCache, so a singleton — the key/TTL/serialization/graceful-degradation all live in
        // the adapter, keeping the handlers oblivious (SRP, like CachedTmdbClient).
        services.AddSingleton<IServedRecommendationCache, ServedRecommendationCache>();

        // The Hangfire precompute job (Milestone 5.3, ADR 0018 §5 — redeeming ADR 0007). Scoped: Hangfire resolves
        // it from a per-execution DI scope. It is the ORCHESTRATOR that dispatches GenerateRecommendationsCommand
        // per active user via ISender in a FRESH child scope each (ADR 0008 — never ISender inside a handler). The
        // Hangfire CLIENT (AddHangfire) + SERVER (AddHangfireServer) + /jobs dashboard live at the Web composition
        // root; only the job body + its Hangfire.Core attributes live here in Infrastructure.
        services.AddScoped<RecommendationPrecomputeJob>();
    }

    /// <summary>
    /// Registers the free local file-storage seam (Milestone 4.1, ADR 0013): the magic-byte
    /// <see cref="ImageContentInspector"/>, the deterministic bucketed-expiry <see cref="AvatarUrlSigner"/>, the
    /// <see cref="LocalFileStorage"/> adapter resolving to both the Application <see cref="IFileStorage"/> port
    /// (upload/mint/delete) and the serving <see cref="IAvatarContentSource"/> seam (read for the media
    /// controller), the <see cref="IUploadPolicy"/> impl over <see cref="FileStorageOptions"/>, a
    /// <see cref="TimeProvider"/> for deterministic expiry, and a hosted service that ensures the upload root
    /// exists at startup. All are stateless singletons.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    private static void AddFileStorage(IServiceCollection services)
    {
        // A system clock for the URL signer's expiry math; overridable in tests. TryAdd so a host that already
        // registered a TimeProvider (or a test override) wins.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IImageContentInspector, ImageContentInspector>();
        services.AddSingleton<AvatarUrlSigner>();

        // One concrete adapter behind two seams: the Application upload port and the local serving source.
        services.AddSingleton<LocalFileStorage>();
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());
        services.AddSingleton<IAvatarContentSource>(sp => sp.GetRequiredService<LocalFileStorage>());

        // Surfaces the size cap + allow-list to the Application-layer avatar validator (dependency rule).
        services.AddSingleton<IUploadPolicy, UploadPolicy>();

        // Ensure App_Data/uploads (outside wwwroot) exists at startup so the first upload never fails.
        services.AddHostedService<UploadStorageInitializer>();
    }

    /// <summary>
    /// Registers the TMDB integration: the raw <see cref="TmdbClient"/> (Milestone 2.0, ADR 0006) as a typed
    /// <see cref="HttpClient"/> with a v4 Bearer default header and the free standard resilience pipeline;
    /// the free in-memory <see cref="IDistributedCache"/>; the Milestone 2.1 cache-aside
    /// <see cref="CachedTmdbClient"/> to which <see cref="ITmdbClient"/> now resolves (wrapping the raw
    /// client, ADR 0007); and the singleton <see cref="ITmdbImageUrlBuilder"/>.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    private static void AddTmdb(IServiceCollection services)
    {
        // Raw TMDB HTTP adapter as a typed client: base address from TmdbOptions.BaseUrl and a v4 Bearer
        // default header from TmdbOptions.ApiKey (keeps the credential out of URLs and logs), wrapped by the
        // free, first-party standard resilience pipeline (rate limiter, total + per-attempt timeouts, retry
        // with backoff honoring Retry-After on 429, circuit breaker). Registered against the CONCRETE
        // TmdbClient so the CachedTmdbClient decorator can wrap it.
        services.AddHttpClient<TmdbClient>((sp, http) =>
        {
            var tmdb = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;
            http.BaseAddress = new Uri(tmdb.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tmdb.ApiKey);
        })
        .AddStandardResilienceHandler();

        // Free, built-in in-memory IDistributedCache (ADR 0004/0007) — no Redis, no Azure, no package. The
        // IDistributedCache seam lets a Redis-compatible provider be swapped in later at registration only.
        services.AddDistributedMemoryCache();

        // SEAM (Milestone 2.1, ADR 0007): ITmdbClient resolves to the cache-aside decorator wrapping the raw
        // TmdbClient over IDistributedCache. Handlers depend only on ITmdbClient and are oblivious to caching.
        services.AddScoped<ITmdbClient>(sp => new CachedTmdbClient(
            sp.GetRequiredService<TmdbClient>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            sp.GetRequiredService<ILogger<CachedTmdbClient>>()));

        // The image-URL builder is stateless (reads only ImageBaseUrl), so a singleton is correct.
        services.AddSingleton<ITmdbImageUrlBuilder, TmdbImageUrlBuilder>();
    }
}
