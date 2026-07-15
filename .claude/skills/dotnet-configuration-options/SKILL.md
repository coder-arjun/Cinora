---
name: dotnet-configuration-options
description: Use when adding settings classes, binding configuration sections, choosing IOptions vs IOptionsSnapshot vs IOptionsMonitor, managing secrets across environments, or reviewing code that reads IConfiguration directly in Cinora.
---

# Configuration & Options Pattern

## Overview
Every configuration section binds to a strongly-typed, validated options class; no service reads `IConfiguration` directly, and no secret ever appears in source or appsettings.json.

## Quick Reference
| Task | Approach |
|---|---|
| Settings classes | `TmdbOptions`, `OpenAIOptions`, `RedisOptions`, `BlobStorageOptions` — one per external concern |
| Bind + validate | `AddOptions<T>().BindConfiguration(T.SectionName).ValidateDataAnnotations().ValidateOnStart()` |
| Singleton consumers | `IOptions<T>` — value fixed at startup (the default choice) |
| Per-request reload | `IOptionsSnapshot<T>` — scoped; re-reads on config change |
| Long-lived + change alerts | `IOptionsMonitor<T>` — singletons that must see updates (`OnChange`) |
| Secrets in dev | `dotnet user-secrets set "Tmdb:ApiKey" "..."` — never in appsettings.json |
| Secrets in prod | Azure App Configuration / Key Vault or environment variables (`Tmdb__ApiKey`) |
| Non-secret defaults | appsettings.json / appsettings.{Environment}.json |

## Pattern
```csharp
// Cinora.Infrastructure/Options/TmdbOptions.cs
public class TmdbOptions
{
    public const string SectionName = "Tmdb"; // WHY: one string constant, no magic strings at bind sites

    [Required(AllowEmptyStrings = false)] // WHY: with ValidateOnStart, a missing key fails deployment, not the first user request
    public string ApiKey { get; set; } = string.Empty;

    [Required, Url]
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";

    [Range(1, 100)]
    public int MaxConcurrentRequests { get; set; } = 10;
}

// Registration (inside AddInfrastructure):
services.AddOptions<TmdbOptions>()
    .BindConfiguration(TmdbOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart(); // WHY: eager validation at boot — fail fast, loudly

// Consumption — inject the options type, never IConfiguration:
public class TmdbClient(HttpClient http, IOptions<TmdbOptions> options)
{
    private readonly TmdbOptions _options = options.Value;
    // _options.ApiKey, _options.BaseUrl ...
}
```

Repeat the same shape for `OpenAIOptions` (`OpenAI` section), `RedisOptions` (`Redis`), and `BlobStorageOptions` (`BlobStorage`).

## Choosing the Interface
Default to `IOptions<T>`. Reach for `IOptionsSnapshot<T>` only when a scoped service genuinely needs hot-reloaded values, and `IOptionsMonitor<T>` when a singleton (e.g., a background job, cached HTTP client wrapper) must react to changes. Complex cross-property rules go in a custom `IValidateOptions<T>` instead of data annotations.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Injecting `IConfiguration` into services | Bind to an options class; inject `IOptions<T>` |
| API keys committed in appsettings.json | user-secrets in dev; Key Vault/App Config or env vars in prod |
| `IOptionsSnapshot<T>` in a singleton | Runtime error — use `IOptions<T>` or `IOptionsMonitor<T>` |
| Skipping `ValidateOnStart()` | Misconfiguration surfaces mid-request instead of at deploy time |
| `configuration["Tmdb:ApiKey"]` string lookups scattered around | One typed class, one `SectionName` constant |
| Options classes in Domain | They are infrastructure/application concerns; see `.claude/skills/clean-architecture-dotnet/SKILL.md` |
