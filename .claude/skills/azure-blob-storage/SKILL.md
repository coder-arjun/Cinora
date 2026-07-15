---
name: azure-blob-storage
description: Use when storing or serving user-uploaded media in Cinora — avatar/poster uploads behind the IFileStorage port, upload validation, path-traversal safety, or free local development against the local filesystem or the Azurite emulator.
---

# File Storage (IFileStorage — free, no Docker)

## Overview
Cinora stores binary media (avatars, uploaded posters) behind an `IFileStorage` port (`SaveAsync`/`GetUrl`/`DeleteAsync`); SQL Server stores only the file name/key, never the bytes. The free, no-Docker providers are a **local filesystem** provider (writes under `wwwroot`/app uploads, served via a controller or static path) for real use, and **Azurite** (`npm install -g azurite`, run `azurite` — no Docker) as the free Azure-Storage emulator for exercising the Blob SDK path in dev. **Azure Blob Storage itself is a paid service and is NOT provisioned.** Uploads are validated server-side; a Blob-backed `IFileStorage` can be added later against the same port.

## Quick Reference
| Task | Approach |
|---|---|
| Abstraction | `IFileStorage` port — `SaveAsync`/`GetUrl`/`DeleteAsync`; handlers depend on it, not `BlobClient` |
| Free default (prod + dev) | `LocalFileStorage` — writes under `wwwroot/uploads`, served via static files / a controller |
| Free Blob-SDK dev path | Azurite emulator (`npm i -g azurite`, `azurite`), connection string `UseDevelopmentStorage=true` |
| Paid (NOT used) | Azure Blob Storage account — the port allows adding it later, but do not provision it |
| Upload safety | Content-type allowlist + size cap + server-generated file name (never a client path) |
| Serving | Static path for public avatars; an authorizing controller action for private files |

## Pattern
```csharp
// The port lives in Application; handlers depend on this, never on a provider type.
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct);
    string GetUrl(string fileName);
    Task DeleteAsync(string fileName, CancellationToken ct);
}

// FREE default — no Docker, no Azure account. A Blob-backed IFileStorage can be added
// later against this same port without touching handlers.
public sealed class LocalFileStorage(IWebHostEnvironment env, IOptions<MediaOptions> options)
    : IFileStorage
{
    private static readonly Dictionary<string, string> Allowed = new()
        { ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp" };

    public async Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct)
    {
        // WHY: client-declared Content-Type is attacker-controlled — allowlist it AND
        // cap size before writing (also sniff magic bytes upstream)
        if (!Allowed.TryGetValue(contentType, out var ext))
            throw new InvalidMediaException("Only JPEG, PNG, or WebP files are allowed.");
        if (content.Length > options.Value.MaxUploadBytes)
            throw new InvalidMediaException("File exceeds the size limit.");

        // WHY: generate the name — never build a path from client input (path traversal)
        var fileName = $"{Guid.NewGuid():N}{ext}";
        var dir = Path.Combine(env.WebRootPath, "uploads");
        Directory.CreateDirectory(dir);
        await using var fs = File.Create(Path.Combine(dir, fileName));
        await content.CopyToAsync(fs, ct);
        return fileName; // store the name in SQL; build URLs at read time
    }

    public string GetUrl(string fileName) => $"/uploads/{Path.GetFileName(fileName)}";

    public Task DeleteAsync(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(env.WebRootPath, "uploads", Path.GetFileName(fileName));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
// Program.cs: builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
```

## Azurite and swapping providers
`LocalFileStorage` is the free default for real use. To exercise the Azure Blob SDK path in dev without cost or Docker, run the **Azurite** emulator (`npm install -g azurite`, then `azurite`) and point a Blob-backed `IFileStorage` at `UseDevelopmentStorage=true`. Because handlers depend only on `IFileStorage`, adding a real Blob provider later (paid, not provisioned now) is a one-line DI swap. Keep private files behind an authorizing controller action rather than a public path, and append a cache-busting version query (`?v={ticks}`) when an avatar changes.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Provisioning a paid Azure Storage account | Use the free `LocalFileStorage` provider, or the Azurite emulator in dev |
| Serving uploads from an unvalidated path (path traversal) | Validate input and use a server-generated file name; never trust client paths/names |
| Coupling handlers to `BlobClient` | Depend on the `IFileStorage` port; keep provider types in Infrastructure |
| Trusting client `Content-Type` alone | Validate against an allowlist and sniff magic bytes (see `.claude/skills/security-hardening/SKILL.md`) |
| Storing bytes or full URLs in the database | Store the file name/key; build URLs at read time |
| Serving private files from a public folder | Gate private downloads behind an authorizing controller action |
