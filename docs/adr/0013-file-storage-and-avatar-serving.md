# ADR 0013 — `IFileStorage` Port, Local Filesystem Provider, and Time-Limited Avatar Serving

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 4 (Watchlists & Profile), Milestone 4.1–4.2 (avatar upload & delivery)
- **Deciders:** architecture-agent (pre-implementation design), security-agent (consulted — upload path &
  token model), orchestrator (ratify)

## Context

Phase 4 introduces the first **user file uploads** in Cinora: profile avatars. The phase brief specifies
"avatar upload to **Azure Blob Storage** with **SAS delivery**." Two hard constraints collide with that:

1. **Free / local-only, no Docker (ADR 0004).** Azure Blob is a paid service; the Azurite emulator needs
   Docker in most setups. Cinora ships **only free, local, no-Docker** components (LocalDB,
   `AddDistributedMemoryCache`, self-hosted SignalR, …), keeping the *port* so a paid provider could be
   swapped later but shipping only the free adapter.
2. **The dependency rule (ADR 0001).** Upload/storage is an external concern: the abstraction belongs in
   Application, the adapter in Infrastructure. Handlers and views must not depend on a storage SDK or a Web
   type (`IFormFile`).

Uploads are also the highest-value **attack surface** added so far — content-type spoofing, path traversal,
image-as-XSS (SVG/polyglots), and unbounded-size DoS all apply. The design must close these with the
free toolset (no image-decoding library, because `SixLabors.ImageSharp`'s *Six Labors Split License* is
commercial for many uses — the same licensing posture that banned MediatR and FluentAssertions).

Finally, the brief's "SAS delivery" implies **time-limited, capability-based URLs** rather than a plain
static path. `FileStorageOptions` already exists (Phase 1) with the free-local design (`Provider="Local"`,
`LocalRootPath="App_Data/uploads"` outside `wwwroot`, `PublicBasePath="/uploads"`, `MaxUploadBytes` ~5 MB,
`AllowedContentTypes` jpeg/png/webp) but **no consuming code** — this ADR defines that consumer.

## Decision

**Introduce an `IFileStorage` port (Application) with a `LocalFileStorage` adapter (Infrastructure) that
writes under `App_Data/uploads` (outside `wwwroot`) using server-generated keys, validates uploads by
magic-bytes + size + allow-list, and serves objects through a same-origin, time-limited signed URL — the
free local stand-in for a Blob SAS.**

### 1. The port (Application), Blob-shaped

```csharp
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string declaredContentType, CancellationToken ct); // → storage key
    string       GetUrl(string storageKey, TimeSpan ttl);   // synchronous, I/O-free — SAS stand-in
    Task         DeleteAsync(string storageKey, CancellationToken ct);
}
```

- Speaks **streams, content-types, opaque keys** — never `IFormFile`, `FileInfo`, or a `BlobClient`. The Web
  controller unwraps `IFormFile` → `Stream` + `ContentType` before dispatch.
- `GetUrl` is **synchronous and I/O-free** (it only computes a signed URL string) so Razor views/tag-helpers
  may call it per-avatar without an async hop.
- A future **Azure Blob adapter** maps the same key to a blob name and `GetUrl` to a real SAS — **zero caller
  changes**. Only the free `LocalFileStorage` ships now.

### 2. Local adapter — storage, keys, path-traversal safety

- **Root:** `FileStorageOptions.LocalRootPath` (`App_Data/uploads`) resolved relative to the content root,
  **outside `wwwroot`**, so nothing under it is web-servable except via the serving route (§4). The adapter
  ensures the directory exists at startup.
- **Server-generated key — never a user path/filename.** `SaveAsync` mints `avatars/{Guid:N}{ext}` where
  `ext` comes from the **magic-byte-confirmed** type, not the client filename or declared type. The original
  filename is discarded → **no user-controlled path component** → path traversal is impossible by
  construction.
- **Read/delete re-validate the key** against `^avatars/[0-9a-f]{32}\.(jpg|png|webp)$` and a canonical
  root-containment check; a malformed/`..` key is rejected, never a filesystem escape.

### 3. Upload validation — twice (friendly + authoritative)

- **FluentValidation (Application):** the declared content-type ∈ allow-list and length ≤ cap, read from a
  small **`IUploadPolicy`** Application port (impl in Infrastructure over `FileStorageOptions`) so the
  validator stays in Application without referencing the Infrastructure Options type. Friendly `400`.
- **`LocalFileStorage.SaveAsync` (authoritative):** does **not** trust the declared type. It caps the copy at
  `MaxUploadBytes` (abort past it), **sniffs the actual bytes** (JPEG `FF D8 FF`, PNG
  `89 50 4E 47 0D 0A 1A 0A`, WebP `RIFF….WEBP`) to derive the true type, and rejects anything else — **SVG and
  all non-images included** — via `ValidationException` → 400. Combined with a forced serving `Content-Type`
  + `X-Content-Type-Options: nosniff` under the strict `script-src 'self'` CSP, a crafted polyglot cannot
  execute, so **magic-byte validation is a sufficient free defense without a decode/re-encode library**.

### 4. Serving — a same-origin, time-limited signed URL (SAS stand-in)

- **Route:** `GET {PublicBasePath}/avatar?t={token}` handled by a Web `MediaController` action,
  `[AllowAnonymous]` (the **token is the capability**, not the auth cookie — public Details reviews show
  reviewer avatars anonymously, exactly as a SAS grants access without a session).
- **Token:** deterministic **HMAC-SHA256 over `{storageKey}|{expiryUnix}`** with a config-sourced signing
  key (`FileStorage:UrlSigningKey`). The verifier recomputes the HMAC, rejects a mismatch or past expiry
  (→ 404), re-validates the key (§2), and streams the bytes with the true `Content-Type`,
  `X-Content-Type-Options: nosniff`, `Content-Disposition: inline`, `Cache-Control: private, max-age=…`, and
  a strong `ETag` (content hash).
- **Bucketed expiry for cacheability:** `expiryUnix` is rounded up to a coarse bucket (default 24 h) so a
  key's URL is **byte-identical within the bucket** → browser-cacheable across a feed/watchlist page, while
  still expiring/rotating each bucket (a rolling SAS).
- **No CSP widening:** the route is same-origin ⇒ `img-src 'self'` already covers avatars — **no change** to
  `SecurityHeadersMiddleware`. The endpoint carries the `PublicRead` per-IP rate limit.
- **Replace/remove cleanup:** on avatar change the old file is deleted **best-effort** after the new key is
  committed to `User.AvatarFileKey`; a delete failure logs a Warning and never fails the request (orphan
  sweep deferred).

### 5. Options & startup

`FileStorageOptions.ValidateOnStart()` is switched **ON** (first consumer this phase — mirrors Phase 2
flipping `TmdbOptions`); a new `UrlSigningKey` is added (user-secrets/env in Production; ephemeral-generated
in Dev/Testing). No DB schema change — `User.AvatarFileKey` exists since Phase 1.

## Consequences

**Positive**
- Free, local, no-Docker avatar storage behind a Blob-shaped port; Azure Blob (or S3/MinIO) is a later
  drop-in with **no caller changes**.
- The dependency rule holds (port in Application, adapter + Options + crypto in Infrastructure/Web); the
  whole avatar-change flow is unit-testable with a fake `IFileStorage`.
- A defense-in-depth upload gate (declared allow-list → magic-byte true-type → size cap → server key → not
  in `wwwroot` → forced type + `nosniff` + strict CSP) with **no new dependency** and **no CSP widening**.
- Time-limited, cacheable capability URLs honor the brief's "SAS delivery" and the exit criterion.

**Negative / accepted costs**
- Two new ports (`IFileStorage`, `IUploadPolicy`), one adapter, a magic-byte inspector, a `MediaController`,
  and a signing key to manage (low-sensitivity — public images).
- **No image re-encode/resize/thumbnail** (no library) → avatars are served at original bytes; mitigated by
  the size cap + caching; a genuinely-free codec or an optional paid swap is a later call.
- A replaced avatar stays reachable via an old bucketed URL until the bucket rolls (≤ 24 h) — acceptable for
  public avatars; best-effort delete can orphan a file (sweep deferred).

## Alternatives considered

1. **Azure Blob + real SAS now.** Rejected: paid, violates ADR 0004. The port keeps it a future swap.
2. **`ITimeLimitedDataProtector` tokens** (built-in, zero key-management). Rejected as the primary: its
   output is **non-deterministic**, so the avatar URL changes every render → browsers re-download every
   avatar on every navigation (no re-encode to shrink them → costly on a feed). Kept as a fallback where a
   signing key is undesirable and TTLs are short.
3. **A plain static-file mapping `/uploads` → `App_Data/uploads` (stable, unsigned URL).** Rejected: it
   ignores the port's `ttl` (a dishonest signature), fails the "time-limited URL" exit criterion, and permits
   hotlink/enumeration. Acceptable only if the product later declares avatars fully public and drops SAS
   parity.
4. **`SixLabors.ImageSharp` decode/re-encode/thumbnail.** Rejected: the *Six Labors Split License* is
   commercial for many uses — the same posture that banned MediatR/FluentAssertions. Magic-byte validation +
   `nosniff` + strict CSP is a sufficient free defense; re-encode/resize is a deferred, licence-gated
   follow-up.
5. **Ownership/validation split into a resource-based `IAuthorizationHandler`.** Not applicable — avatar edit
   has no cross-user surface (you edit only yourself via `ICurrentUser`, ADR 0009); the serving route's
   capability is the signed token, not a policy.

## Related
- ADR 0001 (layering — ports in Application, adapters in Infrastructure), ADR 0004 (free/local-only —
  replaces Azure Blob), ADR 0008 (controller orchestration — the watchlist toggle reuses it), ADR 0009
  (`ICurrentUser` — the avatar owner is server-resolved), ADR 0014 (watchlist status resolution).
- `docs/architecture/phase-4-watchlists-profile-design.md` §2 (the storage/serving model), §3 (CSP/authZ),
  §4 (profile/avatar verticals).
- `src/Cinora.Infrastructure/Options/FileStorageOptions.cs` (the free-local design this consumes).
- Skills: `.claude/skills/azure-blob-storage/SKILL.md` (upload safety & path-traversal reference),
  `.claude/skills/security-hardening/SKILL.md`, `.claude/skills/dotnet-configuration-options/SKILL.md`,
  `.claude/skills/clean-architecture-dotnet/SKILL.md`.

---

_Design-only ADR authored 2026-07-03 against the Phase-1/2/3 code (verified: `FileStorageOptions` exists with
the free-local design and no consumer; `User.AvatarFileKey`/`SetAvatar` exist; `SecurityHeadersMiddleware`
CSP is `img-src 'self' https://image.tmdb.org data:`; `ValidationException`→400 in `GlobalExceptionHandler`;
no `IFileStorage` type exists in `src/`). No application code written._
