# Cinora — Phase 4 Watchlists & Profile Solution Design (Authoritative)

- **Status:** Accepted for Phase 4 (Watchlists & Profile) — pre-implementation design review
- **Date:** 2026-07-03
- **Owner:** architecture-agent
- **Builds on:** [Phase 1 solution structure](solution-structure.md), [Phase 2 Discovery design](phase-2-discovery-design.md),
  [Phase 3 Reviews & Social design](phase-3-social-design.md)
- **New ADRs:** [0013 `IFileStorage` port, local provider & time-limited avatar serving](../adr/0013-file-storage-and-avatar-serving.md),
  [0014 Watchlist status resolution & toggle orchestration](../adr/0014-watchlist-status-resolution-and-toggle-orchestration.md),
  [0015 Notification preferences deferred to Phase 6](../adr/0015-notification-preferences-deferred.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0003 ApplicationUser placement](../adr/0003-applicationuser-identity-placement.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md),
  [0008 Catalog persistence via commands](../adr/0008-tmdb-persistence-via-commands.md),
  [0009 Resource ownership & the current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md)

This document is the single source of truth for Phase 4 (Watchlists & Profile). It designs the first
**user file uploads** in Cinora (avatars behind an `IFileStorage` port), the watchlist verticals that work
from **every title surface**, the profile-edit / settings surface, the privacy-shaped public-profile
enrichment, the migrations, the UI, and the performance stance, then gives a delegable milestone build
order. Implementation agents follow it; deviations require an ADR.

> **Design-only.** No code was written and no build/test was run producing this document. Every `Verify`
> command in §14 is an acceptance check the *implementing* agent must run and show output for.

> **Governing constraints (unchanged; restated because Phase 4 is the first with user file uploads):**
> - **Free / local-only (ADR 0004).** The phase brief names **Azure Blob Storage with SAS delivery** for
>   avatars. That is a paid service and is **replaced by the free `IFileStorage` port over the LOCAL
>   FILESYSTEM** (`App_Data/uploads`, outside `wwwroot`; the **Azurite** emulator is an optional dev
>   alternative — **no Docker**). **Local time-limited signed URLs stand in for SAS.** Only the free local
>   provider ships; the port stays Blob-shaped so a paid provider *could* be swapped later without touching
>   callers (ADR 0013). **No Azure Blob, no Docker, no paid services.**
> - **Hand-rolled mediator (ADR 0005).** Every "`ISender`/mediator" reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (tests use
>   xUnit `Assert` + NSubstitute).
> - **No new image library.** Server-side image validation is **magic-byte sniffing** (dependency-free); a
>   decode/re-encode/thumbnail library (e.g. `SixLabors.ImageSharp`, whose *Six Labors Split License* is
>   commercial for many uses) is **not introduced** — same posture that banned MediatR/FluentAssertions
>   (§2.3, ADR 0013).
> - **Fail-closed contracts (Phase 1/3).** Global fallback authZ (`[Authorize]` unless `[AllowAnonymous]`);
>   global `AutoValidateAntiforgeryToken` (HTMX writes carry the token via the `RequestVerificationToken`
>   header); strict CSP. Phase 4 honors these and — importantly — needs **no CSP widening** (§3): avatars are
>   served **same-origin**, already covered by `img-src 'self'`.
> - **No generic repository; no business rules in handlers; ownership enforced in the handler → 403
>   (ADR 0009); no external DTO into Domain; no `IConfiguration` injected (bind Options).** All reaffirmed.

---

## 1. What Phase 4 adds and where every concern lives

Phase 4 adds the **first file-upload boundary** (`IFileStorage`), a set of **personal-curation write
verticals** (watchlist), the **owner-only profile/settings** surface, and **privacy-shaped watchlist
highlights** on the public profile. It is built on the **existing Phase-1 entities** — `Watchlist`
(`Add(userId, movieId, status)`, `ChangeStatus(status)`; unique `(UserId, MovieId)`), `User`
(`Rename`, `SetAvatar`, `SetProfileVisibility`; `AvatarFileKey?`, `IsProfilePublic`), `Movie` — so it adds
**no new entity and no column** (§10). The layering and dependency rule are unchanged and inviolable
(ADR 0001).

| Concern | Layer / location | Notes |
|---|---|---|
| **`IFileStorage` port** (`SaveAsync(stream, contentType, ct) → key`, `GetUrl(key, ttl) → url`, `DeleteAsync(key, ct)`) | `Cinora.Application/Common/Interfaces/IFileStorage.cs` | NEW. Blob-shaped so Azure Blob is a later swap. `GetUrl` is **synchronous** (pure URL computation, no I/O) so views/tag-helpers may call it. ADR 0013. |
| **`IUploadPolicy` port** (`long MaxAvatarBytes`, `bool IsAllowedContentType(string)`, `bool TryResolveExtension(string, out string)`) | `Cinora.Application/Common/Interfaces/IUploadPolicy.cs` | NEW. Surfaces the `FileStorageOptions` limits to the **Application FluentValidation validator** without Application referencing Infrastructure Options (dependency rule). ADR 0013 §2.3. |
| **`LocalFileStorage` adapter** (writes under `App_Data/uploads`, server-generated keys, magic-byte + size + allow-list enforcement, signed time-limited `GetUrl`) | `Cinora.Infrastructure/Storage/LocalFileStorage.cs` | NEW. The authoritative upload gate. Reads `IOptions<FileStorageOptions>`. **Never writes into `wwwroot`.** ADR 0013. |
| **`UploadPolicy`** (`IUploadPolicy` impl over `FileStorageOptions`) + **`ImageContentInspector`** (magic-byte sniff) | `Cinora.Infrastructure/Storage/UploadPolicy.cs`, `.../ImageContentInspector.cs` | NEW. Dependency-free image detection (JPEG/PNG/WebP); rejects everything else (incl. SVG). §2.3. |
| **Avatar URL signer** (deterministic, bucketed-expiry token) | inside `LocalFileStorage.GetUrl` + verified by `MediaController` | Same-origin capability URL — the SAS stand-in. ADR 0013 §2.4. |
| **Watchlist verticals** — `SetWatchlistStatusCommand`, `RemoveFromWatchlistCommand`, `GetWatchlistStatusQuery`, `GetWatchlistStatusMapQuery`, `GetMyWatchlistQuery`, `GetWatchlistCountsQuery` | `Cinora.Application/Features/Watchlist/` | Request + handler + validator + VM colocated. Deps: `IAppDbContext` + `ICurrentUser` (writes/personal reads). §5, §6, ADR 0014. |
| **Profile-edit verticals** — `UpdateProfileCommand`, `ChangeAvatarCommand`, `RemoveAvatarCommand` | `Cinora.Application/Features/Profiles/` (extend) | Owner-resolved via `ICurrentUser` (you can only edit *yourself* — no id bound). `ChangeAvatarCommand` deps add `IFileStorage`. §4. |
| **Profile enrichment** — `GetProfileQuery` gains privacy-gated watchlist highlights | `Cinora.Application/Features/Profiles/GetProfileQuery.cs` (extend) + `WatchlistHighlightVm` | Highlights follow the §7.3 relationship tiers — never fetched-then-hidden. §7. |
| **`WatchlistController`** `[Authorize]` — set/remove + the reusable status control + the watchlist page | `Cinora.Web/Controllers/WatchlistController.cs` | Orchestrates `EnsureTitleCachedCommand`→watchlist command (ADR 0008/0014). Returns `_WatchlistControl` / page partials. §5, §6. |
| **`SettingsController`** `[Authorize]` — owner-only profile edit, avatar upload, privacy toggle | `Cinora.Web/Controllers/SettingsController.cs` | `GET/POST /settings/profile`, `POST/DELETE /settings/profile/avatar`. §4. |
| **`MediaController`** `[AllowAnonymous]` — token-gated avatar serving | `Cinora.Web/Controllers/MediaController.cs` | Verifies the time-limited token, re-validates the key (path-traversal safe), streams from `App_Data/uploads` with cache + `nosniff` headers. Anonymous-reachable because public Details reviews show reviewer avatars — the **token** is the capability, not the cookie (SAS parity). ADR 0013 §2.4. |
| **`_WatchlistControl` partial** (reusable status menu) + **`_Avatar` partial / `AvatarTagHelper`** | `Cinora.Web/Views/Shared/`, `Cinora.Web/TagHelpers/` | The control renders on `_TitleCard` (rails/search) and Details; `_Avatar` renders a real avatar (`IFileStorage.GetUrl`) or the monogram fallback everywhere a user appears. §3, §5. |
| **DI wiring** — `IFileStorage`→`LocalFileStorage`, `IUploadPolicy`→`UploadPolicy`, `FileStorageOptions.ValidateOnStart()` **ON**, ensure `App_Data/uploads` exists at startup | `Cinora.Infrastructure/DependencyInjection.cs` (`AddInfrastructure`) | Mirrors Phase 2 flipping `TmdbOptions.ValidateOnStart` ON when first consumed. §2.2. |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no business rules in handlers
(the invariants stay on `Watchlist`/`User`); no `IConfiguration` in services; **no user-controlled file
path or filename ever touches the filesystem** (§2.2); **no `Html.Raw` on user content** (display names,
already Razor-encoded since Phase 3); no external image library (§2.3).

---

## 2. `IFileStorage` — the port, the local provider, and the avatar serving/security model (ADR 0013)

This is the **security-critical** part of Phase 4 and the focus of the `/review-security` gate. It is the
free-local realization of the brief's "avatar upload to Azure Blob (SAS delivery)."

### 2.1 The port (Application)

```csharp
// Cinora.Application/Common/Interfaces/IFileStorage.cs — sketch, not literal
public interface IFileStorage
{
    /// Persists an upload and returns the SERVER-GENERATED storage key. Enforces the size cap,
    /// the content-type allow-list, and a magic-byte image check; throws ValidationException on violation.
    Task<string> SaveAsync(Stream content, string declaredContentType, CancellationToken ct);

    /// Computes a same-origin, time-limited URL that serves the stored object (the SAS stand-in). Pure —
    /// no I/O — so views/tag-helpers may call it. Returns a relative path under FileStorageOptions.PublicBasePath.
    string GetUrl(string storageKey, TimeSpan ttl);

    /// Best-effort deletion of a stored object by key (used on avatar replace/remove). Idempotent.
    Task DeleteAsync(string storageKey, CancellationToken ct);
}
```

- **Port in Application, adapter in Infrastructure** (ADR 0001) — handlers/views depend only on the
  abstraction. The port speaks **streams, content-types, and opaque keys** — never a `FileInfo`, an
  `IFormFile` (a Web type), or an Azure `BlobClient`. `IFormFile` is unwrapped to a `Stream` +
  `ContentType` in the Web controller before dispatch.
- **The key is opaque and server-owned.** Callers never construct or interpret it (§2.2). A future Azure
  Blob adapter maps the same key to a blob name and `GetUrl` to a real SAS — **zero caller changes**.

### 2.2 The local adapter — where and how bytes are stored (path-traversal safe)

- **Root:** `FileStorageOptions.LocalRootPath` (default `App_Data/uploads`), resolved **relative to the
  content root, OUTSIDE `wwwroot`** (the option's doc already mandates this). Nothing under the upload root
  is web-servable except through `MediaController` (§2.4) — a stray executable/HTML upload can never be
  fetched-and-run as a static asset.
- **Server-generated key — never a user path or filename.** On `SaveAsync` the adapter mints
  `avatars/{Guid.NewGuid():N}{ext}`, where `ext` is derived from the **magic-byte-confirmed** type
  (`.jpg`/`.png`/`.webp`), **not** from the client filename or declared content-type. The original filename
  is **discarded**. There is therefore **no user-controlled component in the path** — path traversal is
  impossible by construction.
- **Defense-in-depth on read/delete.** `GetUrl`/`DeleteAsync`/`MediaController` re-validate the key against
  a strict regex (`^avatars/[0-9a-f]{32}\.(jpg|png|webp)$`) and combine it with the root via a canonical
  path check (reject any resolved path that escapes the root). A malformed/`..`-bearing key → reject
  (`404`/no-op), never a filesystem escape.
- **Startup:** `AddInfrastructure` ensures the upload root directory exists (create-if-missing) so the
  first upload does not fail on a missing folder.

### 2.3 Upload safety — validated twice (a `/review-security` gate)

Defense-in-depth: a friendly early reject **and** an authoritative server-side gate.

1. **FluentValidation validator (`ChangeAvatarCommandValidator`, Application).** Checks the **declared**
   content-type against the allow-list and the byte length against the cap. It obtains those values from the
   **`IUploadPolicy` port** (impl in Infrastructure over `FileStorageOptions`) — so the validator stays in
   Application without Application referencing Infrastructure's Options type (dependency rule). A violation
   → `ValidationException` → **400** via the existing pipeline. This is the fast, friendly path.
2. **Authoritative enforcement in `LocalFileStorage.SaveAsync` (Infrastructure).** The declared content-type
   is **not trusted**. The adapter:
   - re-checks the byte length ≤ `MaxUploadBytes` while copying (abort past the cap — never buffer an
     unbounded stream);
   - **inspects the actual bytes** via `ImageContentInspector` (magic-byte sniff: JPEG `FF D8 FF`, PNG
     `89 50 4E 47 0D 0A 1A 0A`, WebP `RIFF....WEBP`) and derives the **true** type; a file whose real bytes
     are not one of the three allowed image types → `ValidationException` → 400, **regardless** of the
     declared content-type. **SVG and everything else are rejected** (SVG is the classic image-as-XSS vector
     and is deliberately absent from the allow-list);
   - only then writes to the server-generated key.
   Because the *actual* type is confirmed and the file is later served with a forced `Content-Type` +
   `X-Content-Type-Options: nosniff` (§2.4) under the strict `script-src 'self'` CSP, a crafted polyglot
   cannot execute — magic-byte validation is a sufficient free defense **without** a decode/re-encode
   library (see the governing-constraints note; `ImageSharp`'s split license is why no such library is
   added). Full decode/thumbnailing is a deferred, licence-gated follow-up (§13).
3. **Request-size guard (Web).** The upload action caps the request body
   (`[RequestSizeLimit]`/`MultipartBodyLengthLimit` aligned to `MaxUploadBytes`) so an oversize upload is
   rejected **before** buffering — a DoS guard layered under the validator/adapter checks.

### 2.4 Serving avatars — the time-limited URL (the SAS stand-in) — DECISION

**Decision: serve avatars through a token-gated `MediaController` route; `IFileStorage.GetUrl(key, ttl)`
returns a same-origin, deterministic, bucketed-expiry signed URL — the local stand-in for a Blob SAS.**

- **Route:** `GET {FileStorageOptions.PublicBasePath}/avatar?t={token}` (default `/uploads/avatar?t=…`),
  handled by `MediaController` (`[AllowAnonymous]` — the token is the capability, cookie auth is not
  required, exactly like a SAS; public Details reviews render reviewer avatars anonymously).
- **Token:** a deterministic **HMAC-SHA256 over `{storageKey}|{expiryUnix}`** with a signing key from
  configuration (`FileStorage:UrlSigningKey`, an **options** addition — user-secrets/env in Production,
  ephemeral-generated in Dev/Testing; flag to security-agent to finalize key management). The verifier
  recomputes the HMAC, rejects a mismatch or a past expiry (→ `404`), re-validates the key regex (§2.2),
  and streams the bytes with `Content-Type` = the file's true type, `X-Content-Type-Options: nosniff`,
  `Content-Disposition: inline`, `Cache-Control: private, max-age=…`, and a strong `ETag` (file content
  hash) for cheap conditional GETs.
- **Bucketed expiry for cacheability.** `expiryUnix` is **rounded up to a coarse bucket** (default 24 h) so
  the URL for a given key is **byte-identical within the bucket window** → fully browser-cacheable (crucial
  on a feed/watchlist page rendering many avatars), while still **expiring and rotating** each bucket — a
  rolling SAS. This is why the token is deterministic HMAC and *not* the non-deterministic
  `ITimeLimitedDataProtector` (which changes the URL every render and would force a full re-download of
  every avatar on every navigation — see ADR 0013 alternatives).
- **CSP — NO widening (confirmed).** The serving route is **same-origin** (`/uploads/avatar…`), so
  `img-src 'self'` (already in the Phase-1/2 CSP) covers it with **no change** to
  `SecurityHeadersMiddleware`. `image.tmdb.org` stays for posters; **no new directive, no new host.** This
  is a deliberate, load-bearing property (like Phase 3's "no CSP widening").
- **Rate limit.** `MediaController.Avatar` is anonymous and streams files → it carries
  `RateLimitingPolicies.PublicRead` (per-IP, the standing rule for anonymous TMDB/media endpoints) to blunt
  enumeration/DoS.

### 2.5 Old-avatar cleanup on replace

`ChangeAvatarCommand` ordering keeps the store and DB consistent with no orphan on the happy path:
1. `newKey = await fileStorage.SaveAsync(stream, contentType, ct)` (validates + writes);
2. load `User` by `currentUser.GetRequiredUserId()`, capture `oldKey = user.AvatarFileKey`, call
   `user.SetAvatar(newKey)`, `SaveChangesAsync`;
3. if step 2 committed and `oldKey` is non-null → `await fileStorage.DeleteAsync(oldKey, ct)` **best-effort**
   (try/catch → `LogWarning`; a delete failure leaves an orphan file but never fails the request — an
   orphan-sweep is a later chore, §13).
- If **step 1** fails → DB untouched, no orphan. If **step 2** fails after step 1 → the just-saved `newKey`
  is best-effort deleted in the catch to avoid an orphan; the DB is unchanged.
- `RemoveAvatarCommand`: load `User`, capture `oldKey`, `SetAvatar(null)`, save, then best-effort delete
  `oldKey`.

---

## 3. Anti-forgery / authZ / CSP contract (all Phase-4 endpoints)

A single restated contract every new endpoint honors (the Phase-1/3 global contracts):

1. **Fail-closed authZ.** `WatchlistController`, `SettingsController` are `[Authorize]`; ownership of the
   mutated resource is the current user, resolved by `ICurrentUser` (ADR 0009) — **never a bound id**.
   Profile edit binds **no user id at all** (you edit *yourself*); the watchlist commands key on
   `(currentUserId, MovieId)`, so a user can only mutate their own entry — a foreign `MovieId` simply
   creates/reads the *current user's* row for that title, never someone else's. The **only** `[AllowAnonymous]`
   Phase-4 endpoint is `MediaController.Avatar` (token-gated, §2.4); the `_WatchlistControl` on an anonymous
   card shows a **sign-in affordance**, never a write.
2. **Anti-forgery.** Every state-changing POST/PUT/DELETE (watchlist set/remove, profile update, avatar
   upload/remove) carries the token via the already-wired `RequestVerificationToken` header (HTMX) — the
   global `AutoValidateAntiforgeryToken` validates it. The **avatar upload is `multipart/form-data`**
   (`hx-encoding="multipart/form-data"` or a plain form with `enctype`); the token rides the header/hidden
   field as usual — **no anti-forgery opt-out, no new wiring**. `MediaController.Avatar` is GET → exempt by
   construction.
3. **CSP — NO widening (§2.4).** Avatars are same-origin ⇒ `img-src 'self'` already covers them.
   `connect-src 'self'` already covers the same-origin HTMX/multipart POSTs. `script-src 'self'` is
   untouched — the `_WatchlistControl` menu uses the existing **`@alpinejs/csp`** name-only build (open/close
   only) with HTMX for the writes. **No directive is widened.**
4. **Rate limiting.** Watchlist writes + avatar upload carry `RateLimitingPolicies.SocialWrite` (per-user);
   `MediaController.Avatar` carries `RateLimitingPolicies.PublicRead` (per-IP). Reuses the existing policies —
   no new policy required.

---

## 4. Milestone 4.2 — Profile edit + avatar upload + settings (contract)

**Scope:** an owner-only settings surface to edit **display name**, upload/replace/remove an **avatar**, and
toggle **privacy** (public / friends-only). Real avatars begin rendering app-wide (§3 `_Avatar`).

### 4.1 Verticals (`Cinora.Application/Features/Profiles/`)

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `UpdateProfileCommand(string DisplayName, bool IsProfilePublic)` | Command (+validator) | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load `User` by `currentUser.GetRequiredUserId()`; `Rename(DisplayName)` + `SetProfileVisibility(IsProfilePublic)`; save. Validator: `DisplayName` required, ≤ `User.DisplayNameMaxLength` (100). The authoritative name guard stays `User.Rename`/`Guard.Required` (domain) — the validator is the friendly 400. |
| `ChangeAvatarCommand(Stream Content, string ContentType, long Length)` | Command (+validator) | `IFileStorage`, `IAppDbContext`, `ICurrentUser` | `ChangeAvatarResult(string AvatarFileKey)` | Orchestrates §2.5 (save→`SetAvatar`→best-effort delete old). Validator (via `IUploadPolicy`): declared type in allow-list, `Length` ≤ cap — friendly 400 before the stream is read. |
| `RemoveAvatarCommand()` | Command | `IFileStorage`, `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load `User`; capture old key; `SetAvatar(null)`; save; best-effort delete. Idempotent (no avatar → no-op). |

- **No id is bound.** The actor is the resource owner; there is no cross-user edit surface, so no `403`
  ownership arm is needed here (unlike reviews). This is the simplest ADR-0009 shape.
- **`ChangeAvatarCommand` carries a `Stream`** unwrapped from `IFormFile` in the controller. The handler
  awaits it within the request; the pipeline behaviors log the request harmlessly (the stream is not
  serialized). Accepted trade-off vs. a controller-side `IFileStorage.SaveAsync` call — keeping the
  save→DB→cleanup sequence in one handler makes the whole avatar-change unit testable with a fake
  `IFileStorage` + `IAppDbContext` (no `WebApplicationFactory`).

### 4.2 Controllers & routes

- **`SettingsController` `[Authorize]`** (`ISender`; `IOptions<FileStorageOptions>` only for the client-side
  size hint):
  - `GET /settings/profile` → the edit view (display name, current avatar via `_Avatar`, privacy toggle,
    upload control). Pre-filled from a small `GetMyProfileSettingsQuery` (or the existing `GetProfileQuery`
    for the owner).
  - `POST /settings/profile` `{ DisplayName, IsProfilePublic }` → `UpdateProfileCommand`; HTMX returns the
    updated header fragment (or the form with a saved toast); no-JS redirects back.
  - `POST /settings/profile/avatar` (`multipart/form-data`, `IFormFile file`) → unwrap to stream, dispatch
    `ChangeAvatarCommand`; return the `_Avatar` fragment (new avatar) to swap; `[RequestSizeLimit]` +
    `[EnableRateLimiting(SocialWrite)]`.
  - `DELETE /settings/profile/avatar` → `RemoveAvatarCommand`; return the monogram `_Avatar` fragment.
- **Ownership:** every action operates on `ICurrentUser` — no route id, no cross-user path.

### 4.3 Privacy semantics (public / friends-only)

The existing `User.IsProfilePublic` **is** the "public / friends-only" toggle — no schema change:
- `IsProfilePublic = true` → a signed-in non-friend sees the public profile (reviews, watchlist highlights);
- `IsProfilePublic = false` → **friends-only**: a signed-in non-friend sees the **limited** tier (name +
  avatar only) — exactly the §7.3 privacy shape already enforced **at the data read** in `GetProfileQuery`.
  The settings toggle simply flips the field; §7 extends the same gate to watchlist highlights.

### 4.4 Test plan (4.2)

| # | Test | Asserts |
|---|---|---|
| P1 | `Update_profile_renames_and_sets_visibility` | POST as A updates A's `DisplayName` + `IsProfilePublic`; row reflects both; a blank/too-long name → 400. |
| P2 | `Avatar_upload_persists_key_and_renders_via_time_limited_url` | A valid JPEG upload → `User.AvatarFileKey` set; the profile renders an `<img>` whose `src` is the same-origin `…/avatar?t=…` token URL; the file exists under `App_Data/uploads`, **not** `wwwroot`. |
| P3 | `Avatar_upload_rejects_bad_type_and_oversize` | A `.svg`/`.txt` (or a `.jpg`-named file whose bytes are HTML) → **400** (magic-byte gate); a > `MaxUploadBytes` upload → 400/413; nothing is written. |
| P4 | `Avatar_replace_deletes_the_old_file` | Uploading a second avatar updates the key and best-effort deletes the previous file; a delete failure still returns 200 + logs a Warning. |
| P5 | `Expired_or_tampered_avatar_token_is_404` | A token past its bucketed expiry, or with a mutated key/HMAC, → 404 (no bytes served); a `..`-bearing key → 404. |
| P6 | `Settings_require_auth` | Anonymous `GET/POST /settings/profile*` → 302 login (fail-closed). |

**Migration:** none — `User.AvatarFileKey`/`IsProfilePublic` exist since Phase 1 (§10).

---

## 5. Milestone 4.3 — Watchlist status control on every title surface (contract, ADR 0014)

**Scope:** add/update status and remove a title from **Details, rails, and search cards**, via one reusable
HTMX control; the per-title status probe and the **batch status map** that keep cards N+1-free.

### 5.1 Verticals (`Cinora.Application/Features/Watchlist/`)

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `SetWatchlistStatusCommand(Guid MovieId, WatchlistStatus Status)` | Command (+validator) | `IAppDbContext`, `ICurrentUser` | `SetWatchlistResult(WatchlistStatus Status, bool WasAdded)` | **Upsert.** Load `(currentUser, MovieId)`; present → `ChangeStatus(Status)`; absent → `Watchlist.Add(currentUser, MovieId, Status)`. Unique `(UserId, MovieId)` race → catch `DbUpdateException`, re-probe, `ChangeStatus` (mirrors `CreateReviewCommand`'s reconcile). Validator: `Enum.IsDefined(Status)`. |
| `RemoveFromWatchlistCommand(Guid MovieId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Remove the `(currentUser, MovieId)` row if present; idempotent (guards `DbUpdateConcurrencyException`). |
| `GetWatchlistStatusQuery(Guid MovieId)` | Query | `IAppDbContext`, `ICurrentUser` | `WatchlistStatus?` | The single-title probe (Details). `null` = not in list / anonymous. `AsNoTracking` scalar. |
| `GetWatchlistStatusMapQuery(IReadOnlyList<int> TmdbIds, MediaType Media)` | Query | `IAppDbContext`, `ICurrentUser` | `IReadOnlyDictionary<int, WatchlistStatus>` | **The N+1 killer for rails/search.** ONE query: `Movies.Where(m => m.MediaType==Media && TmdbIds.Contains(m.TmdbId)).Join(Watchlists on Id==MovieId where UserId==me).Select(tmdbId, status)`. Titles not yet persisted (most rail cards) are simply absent → "not in list". Backed by existing indexes (Movies `(TmdbId, MediaType)` unique + Watchlists `(UserId, MovieId)` unique) — **no new index** (§10). |

- **Ownership is implicit.** Every command/query keys on `currentUser.GetRequiredUserId()` — a user can only
  ever touch their own `(UserId, MovieId)` row. No `ForbiddenAccessException` arm is needed (there is no
  cross-user watchlist surface).
- **The status invariant stays in the domain** (`WatchlistStatus` enum; `Watchlist.Add`/`ChangeStatus`).
  The validator's `Enum.IsDefined` is the friendly 400.

### 5.2 The toggle needs the internal `MovieId` — controller orchestration (ADR 0008/0014)

Cards and the Details page address titles by `(media, tmdbId)`, but `Watchlist` references the internal
`Movie.Id`. So a watchlist write **must persist the title first**, exactly like `ReviewsController.Create`
(§4 of the Phase-3 design). The **controller orchestrates** (never `ISender` inside a handler):

1. `var ensure = await sender.Send(new EnsureTitleCachedCommand(tmdbId, media), ct);`
2. if `ensure.Outcome == NotFound` → `404` (cannot watchlist a title TMDB lacks);
3. else `await sender.Send(new SetWatchlistStatusCommand(ensure.MovieId!.Value, status), ct);`
4. return the re-rendered `_WatchlistControl`.

- On the **watchlist page** (§6) the item already carries its `MovieId`, and `EnsureTitleCachedCommand`
  short-circuits on its `(TmdbId, MediaType)` existence check (the title is already persisted) → **one cheap
  indexed read, no TMDB call** — so routing *all* surface writes through the same `(tmdbId, media)` endpoint
  is DRY and cheap. Remove uses `GetCachedMovieIdQuery(tmdbId, media)` (already exists) → `RemoveFromWatchlistCommand`.

### 5.3 The reusable `_WatchlistControl` (how a card gets status without an N+1)

- **`WatchlistControlVm(int TmdbId, MediaType Media, WatchlistStatus? Status, bool IsAuthenticated)`** (Web
  view model). The partial renders: **authed** → a status menu (Add ▸ Plan to Watch / Watching / Watched,
  and Remove) reflecting `Status`, each option an `hx-post`/`hx-delete` to `WatchlistController`;
  **anonymous** → a bookmark affordance linking to login with `returnUrl`.
- **Rails/Search (batch):** the rail/search **controller** resolves `GetWatchlistStatusMapQuery` **once per
  page** for authed users, then builds a `WatchlistControlVm` per card. `GetTitleRailQuery`/`SearchTitlesQuery`
  stay **`ITmdbClient`-only and CQRS-pure** (unchanged) — the status join is a *separate* batch query the
  controller composes into the view model (`RailViewModel(RailVm Rail, IReadOnlyDictionary<int,WatchlistStatus> Statuses)`).
  Anonymous → the map is skipped, cards show the sign-in affordance, and the rail partial stays cacheable/pure
  (ADR 0014). **No per-card fetch, no N+1.**
- **Details (single):** the Details controller already dispatches `EnsureTitleCachedCommand` after the read
  (first-touch), so it **has the `MovieId`**; it resolves the single `GetWatchlistStatusQuery` (authed) and
  renders `_WatchlistControl` inline — replacing the Phase-2 inert **"Add to Watchlist / Soon" stub**
  (`Details.cshtml` lines 139–151). Anonymous → sign-in affordance.
- **HTML-validity note (frontend-agent):** `_TitleCard` is currently a single `<a>` wrapping the whole card.
  An interactive control cannot nest inside an anchor. Restructure the card so the poster/title is the link
  and the `_WatchlistControl` is an **absolutely-positioned sibling over the poster** (outside the anchor),
  keyboard-reachable, with its own accessible name. §11.

### 5.4 The Watched → review nudge

Marking a title **Watched** surfaces the review-write affordance:
- **On Details:** when `SetWatchlistStatusCommand` returns `Watched` and the user has no review yet, the
  control's response reveals/links the existing lazy **`#my-review`** write region (the `_ReviewWriteForm`
  already lazy-loads via `GET /reviews/mine`). A small "Write your review" CTA in the Watched-state control,
  or an `HX-Trigger` the page's `site.ts` handles to scroll/focus `#my-review`.
- **On cards/rails:** the Watched-state control shows a subtle "Review?" link to
  `…/discover/title/{media}/{tmdbId}#cinora-reviews-heading`.
- Kept intentionally light (a CTA/anchor, no new heavy plumbing); the review form itself is Phase-3 work
  reused. Whether the nudge is a modal vs an inline reveal is a **UX call** (§13).

### 5.5 Controllers & routes (`WatchlistController` `[Authorize]`)

- `POST /watchlist` `{ TmdbId, Media, Status }` → orchestrate (§5.2) → `_WatchlistControl` (HTMX) / redirect
  (no-JS). `[EnableRateLimiting(SocialWrite)]`.
- `DELETE /watchlist` `{ TmdbId, Media }` → `GetCachedMovieIdQuery` → `RemoveFromWatchlistCommand` →
  `_WatchlistControl` (now "Add"). `[EnableRateLimiting(SocialWrite)]`.
- `GET /watchlist/control?tmdbId=&media=` → the `_WatchlistControl` fragment (used to lazily hydrate/refresh
  a card's control if the page prefers per-card lazy over the batch map — the **batch map is the default**;
  this endpoint is the fallback, not the primary path).

### 5.6 Test plan (4.3)

| # | Test | Asserts |
|---|---|---|
| W1 | `Set_status_from_a_card_persists_after_ensuring_the_title` | `POST /watchlist {tmdbId,media,PlanToWatch}` first-touch persists the `Movie` (ADR 0008) then creates the `(me, movieId)` entry; a second POST with `Watched` **updates** the same row (no duplicate; unique index holds). |
| W2 | `Remove_is_idempotent` | `DELETE /watchlist` removes the entry; a second DELETE is a no-op 200; control returns to "Add". |
| W3 | `Status_map_is_one_query_and_marks_only_my_titles` | For a rail of N tmdbIds, `GetWatchlistStatusMapQuery` returns only the current user's statuses; another user's entries never leak; unpersisted titles are absent. |
| W4 | `Watchlist_write_requires_auth_and_token` | Anonymous POST → 302 login; an authed POST missing the anti-forgery token → 400. |
| W5 | `Set_status_actor_is_server_resolved` | A hidden `UserId=other` field is ignored — the entry is stamped with the caller (ADR 0009). |
| W6 | `Watched_response_offers_the_review_nudge` | Setting `Watched` on Details returns a control whose markup links the `#my-review` write region. |

**Migration:** none for the control paths — the `(UserId, MovieId)` unique index + `Movies (TmdbId,
MediaType)` unique already serve every write, probe, and the batch map. Page indexes land in 4.4 (§10).

---

## 6. Milestone 4.4 — The watchlist page (filter, sort, keyset) + counts + empty states (contract)

**Scope:** the current user's watchlist at `GET /watchlist`, filterable by status and sortable, keyset-
paginated, with per-status counts and empty states.

### 6.1 Verticals (`Cinora.Application/Features/Watchlist/`)

> **AMENDMENT (2026-07-06, Phase 4 exit gate):** Milestone 4.4 shipped **`WatchlistSort.RecentlyAdded` ONLY** —
> `RecentlyUpdated` (and its test **WP3** below) are **DEFERRED**. `RecentlyUpdated`
> (`COALESCE(UpdatedAtUtc, AddedAtUtc) DESC`) cannot be served by the two `AddedAtUtc` indexes without a
> per-user scan+sort; adding it needs a `(UserId, UpdatedAtUtc)`-class index (or a persisted `COALESCE` sort
> key), which was intentionally not added (the more YAGNI-correct call). The `Sort` param is threaded but the
> handler currently ignores it. To add it later: introduce the enum value, branch the `OrderBy`, and add the
> index (run the db-agent alone). Tracked in `REVIEW_BACKLOG.md` (from 4.4 + the Phase-4 exit architecture
> gate). The `RecentlyUpdated`/WP3 references in §6.1/§14/§7.1 below are the original design intent, retained
> for that traceability but superseded by this note.

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `GetMyWatchlistQuery(WatchlistStatus? Filter, WatchlistSort Sort, WatchlistCursor? Cursor, int Take = 24)` | Query | `IAppDbContext`, `ICurrentUser` | `WatchlistPageVm` (page of `WatchlistItemVm` + `NextCursor` + `HasMore`) | Keyset over the chosen sort; `Take+1` for `HasMore`; opaque `(sortKey, Id)` cursor — never `OFFSET`. Joins `Movies` for poster/title/year (one `AsNoTracking` query, like the feed). |
| `GetWatchlistCountsQuery()` | Query | `IAppDbContext`, `ICurrentUser` | `WatchlistCountsVm` (`PlanToWatch`, `Watching`, `Watched`, `Total`) | ONE `GROUP BY Status` count for the current user (drives the filter chips + counts). Index-backed by `(UserId, Status)` (§10). |

- **`WatchlistItemVm`** (Application record): `MovieId, TmdbId, Media, Title, PosterPath?, ReleaseYear?,
  Status, AddedAtUtc, UpdatedAtUtc?`. No Domain entity, no EF type. The card reuses the poster/title
  grammar; each item embeds a `_WatchlistControl` (status already known → no extra query).
- **`WatchlistSort`** (enum): `RecentlyAdded` (default, `AddedAtUtc DESC, Id DESC`), `RecentlyUpdated`
  (`COALESCE(UpdatedAtUtc, AddedAtUtc) DESC, Id DESC`). **Title A–Z is deferred** — a keyset over the joined
  `Movie.Title` is cross-table and awkward; if wanted it is a capped/offset mode or a denormalized sort key
  (flagged §13). The two time sorts are keyset-clean and cover the primary UX.
- **Filter + keyset coexist** via the composite index `(UserId, Status, AddedAtUtc)` (status-filtered) and
  `(UserId, AddedAtUtc)` (the "All" view). `GetMyWatchlistQuery` applies the status `Where` before the keyset
  filter so `HasMore`/cursor reflect the filtered set (mirrors `GetTitleReviewsQuery`).

### 6.2 Controllers & routes (`WatchlistController` `[Authorize]`)

- `GET /watchlist?status=&sort=` → the page shell: filter chips (with counts from
  `GetWatchlistCountsQuery`), sort control, and page 1 of `GetMyWatchlistQuery` inline.
- `GET /watchlist/page?status=&sort=&cursorKey=&cursorId=&take=` → the `_WatchlistPage` append fragment
  (self-replacing `revealed` sentinel — the 2.4/3.4 load-more grammar).
- Enum guards on `status`/`sort` (the `Enum.IsDefined` grammar; unparseable → treat as "All"/default, not a
  500).

### 6.3 Empty states + counts (§9 restated here for the page)

- **Totally empty** (no entries) → a premium "Start your watchlist" empty state with a **Discover** CTA (not
  an error).
- **Empty filter** (entries exist, none in the selected status) → a quiet "Nothing marked *Watching* yet"
  with the count chips still shown.
- **Counts** on every filter chip (Plan N · Watching N · Watched N · All N) from `GetWatchlistCountsQuery`.

### 6.4 Test plan (4.4)

| # | Test | Asserts |
|---|---|---|
| WP1 | `Watchlist_page_shows_only_my_entries_keyset` | A sees only A's entries, newest-first; page 2 via the cursor returns strictly older items, no overlap/gap. |
| WP2 | `Filter_by_status_scopes_the_page_and_counts` | `?status=Watched` returns only Watched entries; the count chips report the correct per-status totals. |
| WP3 | `Sort_recently_updated_orders_by_status_change` | Changing an entry's status re-orders it to the top under `sort=RecentlyUpdated`. |
| WP4 | `Empty_and_empty_filter_states_render` | No entries → the Discover empty state; entries but none in the filter → the quiet filtered-empty state; both 200, not errors. |
| WP5 | `Watchlist_page_requires_auth` | Anonymous `/watchlist` → 302 login. |

**Migration (database-agent):** add `IX_Watchlists_UserId_AddedAtUtc` on `(UserId, AddedAtUtc)` and
`IX_Watchlists_UserId_Status_AddedAtUtc` on `(UserId, Status, AddedAtUtc)` (the composite also serves the
`(UserId, Status)` grouped counts by prefix). §10.

---

## 7. Milestone 4.5 — Public profile watchlist highlights (privacy-shaped)

**Scope:** the public `Profile.cshtml` (3.3) gains **watchlist highlights**, gated by the **same §7.3
relationship tiers** as reviews; real avatars render throughout.

- **Extend `GetProfileQuery`** to load, under the existing `canViewReviews` gate (owner / accepted friend /
  public profile), a small curated set of highlights — recommended: up to ~8 recent **Watched** titles
  (posters + titles), newest-first, joined from `Watchlists`→`Movies`. A **limited/private** tier
  (`IsProfilePublic=false`, non-friend) gets **none** — highlights are **never fetched-then-hidden**
  (privacy at the data read, exactly as reviews are). New VM: `WatchlistHighlightVm(int TmdbId, MediaType
  Media, string Title, string? PosterPath)`; `ProfileVm` gains
  `IReadOnlyList<WatchlistHighlightVm> WatchlistHighlights` + a `CanViewWatchlist` flag mirroring
  `CanViewReviews`.
- **Which titles?** Default: recent **Watched** (a "what they've seen" highlight strip). Whether to include
  Plan-to-Watch/Watching, and how many, is a **UX call** (§13); the query shape is the same.
- **Render** on `Profile.cshtml` as a poster strip below the header, only when `CanViewWatchlist` and the
  list is non-empty; empty → omit the section (quiet). Owner sees their own highlights + an edit affordance
  linking to `/watchlist`.
- **Avatars now render** on the profile header, `_ReviewCard`, feed items, and comments via the shared
  `_Avatar` seam (§3) — the Phase-3 monogram becomes the fallback when `AvatarFileKey` is null.

### 7.1 Test plan (4.5)

| # | Test | Asserts |
|---|---|---|
| PH1 | `Private_profile_hides_watchlist_highlights_from_non_friend` | Signed-in non-friend of a `IsProfilePublic=false` owner sees **no** highlights (and the query never fetched them); a friend/public viewer sees them. |
| PH2 | `Highlights_reuse_the_privacy_tier` | The highlight visibility tracks `CanViewReviews`/`CanViewWatchlist` exactly (owner/friend/public = visible; limited = absent). |
| PH3 | `Profile_renders_real_avatar_when_present` | An owner with an avatar renders the token `<img>`; a null key renders the monogram fallback. |

**Migration:** the highlights read is served by `IX_Watchlists_UserId_Status_AddedAtUtc` (4.4); no additional
index. §10.

---

## 8. Notification preferences — DEFERRED to Phase 6 (ADR 0015)

The phase brief lists "Account settings: **notification preferences** (feeds Phase 6 push opt-in)." **There
is no schema field for preferences today** (the `Device` entity is Web-Push transport, not a preference
store), and Phase 4 ships **no push or email delivery** to gate.

> **DECISION (ADR 0015): defer notification preferences to Phase 6**, where Web Push (`Device` + VAPID)
> actually lands and there is a delivery channel for a preference to govern. Building a preferences
> field/toggle now would be YAGNI dead config (nothing reads it), and its correct shape (per-channel:
> push/email/in-app; per-event: like/comment/friend/AI) depends on the Phase-6 push design. The **privacy
> toggle** (public / friends-only) **is** delivered in Phase 4 (§4.3) — that is a *visibility* setting, not a
> *notification-delivery* preference. The Settings page MAY show a disabled "Notification preferences —
> arriving with push (Phase 6)" placeholder for roadmap discoverability, or omit it.

This mirrors the Phase-3 chat deferral (ADR 0010): a PRD/brief item that the current schema does not support
is deferred behind an ADR with its future shape recorded, not silently built. **Flagged to the user** (§13).

---

## 9. Empty states + counts (everywhere watchlist status appears)

A single consolidated stance (frontend-agent + ux-agent):
- **Watchlist page:** per-status count chips (§6.3); totally-empty and empty-filter states (never errors).
- **`_WatchlistControl`:** the not-in-list state reads **"Add to watchlist"**; in-list reads the status with
  a way to change/remove. Anonymous → "Sign in to save".
- **Profile highlights:** empty → the section is omitted (quiet), not an error stub.
- **Feed/review cards:** unaffected counts; avatars gain a monogram fallback (never a broken image).
- **Nav:** a **"Watchlist"** link for signed-in users (alongside Home/Discover/Friends).

---

## 10. Migrations / schema deltas (for database-agent — do NOT write here)

**No new entity and no column change** — every Phase-4 feature uses existing Phase-1 entities/columns
(`Watchlist`, `WatchlistStatus`, `User.AvatarFileKey`, `User.IsProfilePublic`). Only **indexes** are added
(index-only migrations, consistent with all of Phase 3). Confirm and add:

| Milestone | Add index | On | Why | Existing (unchanged) |
|---|---|---|---|---|
| 4.4 | `IX_Watchlists_UserId_AddedAtUtc` | `Watchlist(UserId, AddedAtUtc)` | the "All" watchlist page keyset (recently-added) | **unique `(UserId, MovieId)` — CONFIRMED present** (`WatchlistConfiguration`); Restrict FKs to `User`/`Movie` |
| 4.4 | `IX_Watchlists_UserId_Status_AddedAtUtc` | `Watchlist(UserId, Status, AddedAtUtc)` | status-filtered page keyset **and** the `(UserId, Status)` grouped counts (by prefix) | as above |
| 4.2 / 4.3 / 4.5 | **none** | — | avatars reuse `User.AvatarFileKey`; the status probe/map + set/remove are served by the existing unique `(UserId, MovieId)` + `Movies (TmdbId, MediaType)` | — |

- **Clustered `Id` is appended to each keyset index** implicitly (SQL Server) → the `(sortKey, Id)`
  tie-break is index-supported (same rationale as `FeedIndex`). No `INCLUDE` needed (the page projection
  joins `Movies` by PK).
- Prefer a single small `Phase4WatchlistIndexes` migration (both indexes) generated in
  `Cinora.Infrastructure` (startup project `Cinora.Web`); apply via the reviewed **idempotent SQL script** /
  `db-update.ps1` (never auto-migrate). `CinoraTest` gets it automatically; **`CinoraDev` apply is carried**
  with the standing Express-unreachable caveat (PROGRESS.md open item #1).

**Config additions (not schema):** `FileStorage:UrlSigningKey` (avatar-URL HMAC key, §2.4) and switching
`FileStorageOptions.ValidateOnStart()` **ON** (first consumer this phase). Options change, not a migration.

**Honest schema-insufficiency flags:**
1. **No notification-preferences field** — deferred to Phase 6 (§8, ADR 0015).
2. **No denormalized watchlist sort key** — Title A–Z sort is deferred (cross-table keyset; §6.1, §13).
3. **Avatar files are unmanaged on best-effort delete failure** — an orphan-sweep is a later chore (§13).

---

## 11. UI screens

(frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`, `alpine-htmx-interactivity`,
`ui-animations`, `responsive-accessibility`, `azure-blob-storage` for the upload-safety reference.)

- **`_WatchlistControl`** — a compact status control: a primary button showing the current state (bookmark
  "Add to watchlist" ▸ or "Plan to Watch/Watching/Watched") + a menu (name-only `@alpinejs/csp` for
  open/close) whose options are `hx-post`/`hx-delete` writes returning the re-rendered control. On
  `_TitleCard` it is an **absolutely-positioned sibling over the poster** (not nested in the card anchor —
  §5.3); on Details it replaces the Phase-2 "Soon" stub. Instant HTMX swap = the "instant UI feedback" exit
  criterion. Reduced-motion respected; the menu is keyboard + screen-reader navigable; the control announces
  its result via a scoped `#watchlist-status` polite region (the region-scoped `site.ts` a11y pattern from
  3.1–3.5).
- **Watchlist page (`/watchlist`)** — filter chips with counts, a sort control, a responsive poster grid of
  `WatchlistItemVm` cards each with its `_WatchlistControl`, keyset load-more (self-replacing `revealed`
  sentinel), and the empty/empty-filter states.
- **Settings (`/settings/profile`)** — display-name field, the current avatar (`_Avatar`) with an
  upload/replace control (`multipart/form-data`; client-side size/type hint from `FileStorageOptions`, but
  the server is authoritative), a remove-avatar action, and the **public / friends-only** privacy toggle;
  the optional disabled "Notification preferences (Phase 6)" placeholder (§8).
- **Profile (`/users/{id}`)** — the real avatar in the header (monogram fallback); a privacy-shaped
  **watchlist-highlights** poster strip (§7); the owner sees an "Edit profile" link to `/settings/profile`.
- **`_Avatar` seam** — one partial/tag-helper renders `AvatarFileKey?` → `IFileStorage.GetUrl(key, ttl)`
  (`<img loading="lazy" decoding="async">`) or the monogram, reused on profile/`_ReviewCard`/feed/comments
  (retires the Phase-3 monogram-only render; closes the REVIEW_BACKLOG 3.4 "avatar never rendered" Low).
- **A11y:** HTMX-swapped control/page regions carry scoped focus-restore + a polite status announce (the
  established `site.ts` region pattern); the avatar upload input has a visible label + describes accepted
  types/size; the file-upload error surfaces inline (`role="alert"`), never a raw ProblemDetails page for
  the HTMX path.

**CSP/contract extensions:** **none** (§3). Load-bearing: Phase 4 adds file uploads and avatar delivery
**without touching `SecurityHeadersMiddleware`.**

---

## 12. Performance stance

(performance-agent; skills: `dotnet-performance`, `ef-core-data-access`.)

- **No N+1 on title surfaces:** the **batch `GetWatchlistStatusMapQuery`** resolves a whole rail/search
  page's statuses in **one** query (§5.3); the Details/watchlist-page items already know their status. Rail
  query stays `ITmdbClient`-only and cacheable; the status join is a separate, per-page, authed-only read.
- **Keyset pagination** on the watchlist page (opaque `(sortKey, Id)`, `Take+1`, never `OFFSET`), backed by
  the §10 composite indexes; per-status **counts in one `GROUP BY`** query.
- **`AsNoTracking().Select(...)` projections** for every read; the page/highlights join `Movies` by PK in a
  single round-trip (no N+1), same shape as the 3.4 feed.
- **Watchlist writes:** load a tracked entry → domain method → `SaveChanges`; unique-index race caught and
  reconciled (reuse the `CreateReview` pattern). The `EnsureTitleCachedCommand` orchestration is **one cheap
  indexed existence check** when the title is already cached (the common case for a watchlist toggle).
- **Avatar delivery is cacheable:** deterministic **bucketed-expiry** URLs (§2.4) + `Cache-Control` + strong
  `ETag` (content hash) → browsers reuse avatar bytes across a feed/watchlist page instead of re-downloading
  per render. The serving route streams from disk **async** with a `CancellationToken`.
- **Upload path is bounded:** the request-size guard rejects oversize **before** buffering; the adapter
  aborts a copy past `MaxUploadBytes`; magic-byte inspection reads only the header bytes.
- **Async end-to-end**, `CancellationToken` threaded controller → `ISender` → handler → EF/`IFileStorage`
  (no `.Result`, no sync-over-async). `GetUrl` is intentionally **sync + I/O-free** so it is safe to call
  per-avatar in a view.

---

## 13. Key risks and product decisions

**Risks baked into the design:**
- **Upload abuse is the headline risk.** Mitigated in depth (§2.3): declared-type allow-list (validator) →
  **magic-byte** true-type check + size cap (adapter, authoritative) → **server-generated key** (no user
  path) → **not under `wwwroot`** → served with **forced content-type + `nosniff`** under strict CSP. SVG
  and non-image types are rejected outright. The residual (a valid-but-large image served un-resized, or a
  benign polyglot that cannot execute) is accepted; full decode/re-encode/thumbnail is deferred (needs an
  image library — `ImageSharp`'s split licence is why none is added; a genuinely-free codec or an optional
  paid-swap is a later call).
- **Avatar URL cacheability vs. expiry.** A per-render-unique URL would defeat browser caching and re-download
  every avatar on a feed. Mitigated by the **deterministic bucketed-expiry** token (§2.4). Trade-off: an
  avatar remains reachable via an old URL until the bucket rolls (≤ the bucket, default 24 h) even after
  replacement — acceptable for public avatars; documented in ADR 0013.
- **URL-signing key management.** The HMAC key gates who can mint avatar URLs (low-sensitivity: public
  images). Prod key in user-secrets/env; Dev/Testing may auto-generate (ephemeral → tokens invalidate on
  restart, harmless in dev). Data-Protection key-ring persistence noted for the Web host. Finalize with
  security-agent.
- **`EnsureTitleCached` on every surface write** puts a first-touch persistence on the request path. Cheap
  when already cached (one indexed read); the ADR-0007/0008 background-enqueue escape (Hangfire, Phase 5/6)
  applies if it ever matters.
- **Best-effort avatar cleanup** can orphan a file if the delete fails after a successful replace — an
  orphan-sweep chore (a later Hangfire job) reconciles; never blocks the user.

**Genuine PRODUCT / UX decisions (defer to the user or ux-agent — NOT architecture):**
- **Notification preferences in Phase 4?** Recommend **defer to Phase 6** (ADR 0015) — no schema field, no
  delivery channel yet. **Flag to user.**
- **Avatar time-limited URLs vs. a plain stable `/uploads` URL.** Design ships the **time-limited** model
  (honors the exit criterion + keeps the port SAS-shaped); a stable unsigned URL is simpler but ignores the
  port `ttl` and permits hotlinking — revisit only if the product declares avatars fully public (ADR 0013
  alternatives).
- **Watchlist highlights content** (recent Watched only vs. include Plan/Watching; how many) — a curation/UX
  call (§7).
- **Watchlist sorts** — RecentlyAdded/RecentlyUpdated ship; **Title A–Z deferred** (cross-table keyset). UX
  may prioritize it → then a denormalized sort key or a capped offset mode.
- **Watched→review nudge presentation** — inline reveal vs. modal vs. card link (§5.4).
- **Default watchlist status on first add** (PlanToWatch vs. an explicit picker) — UX default; recommend the
  control adds as **Plan to Watch** by default with a one-tap change.
- **Max avatar size / accepted types** — `FileStorageOptions` defaults (5 MB; jpeg/png/webp) are overridable;
  consider lowering the cap (e.g. 2 MB) since avatars are not re-encoded.

---

## 14. Phase 4 milestone build order (delegable)

Ordered milestones for the orchestrator. Each states the owning agent(s), the deliverable, the exact
`Verify` command/acceptance, and the review gates (`/review-architecture`, `/review-code`,
`/review-security`, `/review-performance`, `/review-ui` — automatic after every milestone and at phase end).
All commands run from the repo root. Phase 4 is **mock-first** (faked `ITmdbClient`; no live TMDB call
needed — the key is already set) and runs integration tests against real **LocalDB `CinoraTest`**. No new
external credential is required.

> **Dependency order:** 4.1 (`IFileStorage` foundation) → 4.2 (profile/avatar, consumes 4.1) → 4.3
> (watchlist control everywhere) → 4.4 (watchlist page) → 4.5 (profile highlights, consumes 4.2 avatars +
> 4.4 watchlist). 4.1 is first because it is the highest-risk, security-critical seam and needs
> architecture + security sign-off before anything renders an avatar.

### 4.1 — `IFileStorage` port + local adapter + upload safety + token serving
- **Owner:** backend-agent + security-agent (upload path + token model) + architecture-agent sign-off on the
  port/adapter boundary and the serving model (ADR 0013).
- **Deliverable:** `IFileStorage` + `IUploadPolicy` (Application); `LocalFileStorage` + `UploadPolicy` +
  `ImageContentInspector` (Infrastructure, magic-byte); `MediaController` token-gated serving
  (`[AllowAnonymous]` + `PublicRead`); DI wiring + `FileStorageOptions.ValidateOnStart()` ON + upload-root
  creation + `FileStorage:UrlSigningKey`. No UI beyond wiring.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Infrastructure.Tests/Cinora.Infrastructure.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Round-trip `SaveAsync`→`GetUrl`→serve→`DeleteAsync`; **bad-type/oversize/`..`-key rejected**; the file
  lands under `App_Data/uploads` (**not** `wwwroot`); an **expired/tampered token → 404**; **CSP unchanged**
  (`img-src 'self' https://image.tmdb.org data:`, no new host). Build clean (TWAE on). Then
  `/review-security` (the upload-path gate), `/review-architecture` (port placement), `/review-code`.

### 4.2 — Profile edit + avatar upload + settings (privacy)
- **Owner:** backend-agent + frontend-agent + ux-agent + security-agent (upload UI/anti-forgery).
- **Deliverable:** `UpdateProfileCommand`, `ChangeAvatarCommand`, `RemoveAvatarCommand` (+validators via
  `IUploadPolicy`); `SettingsController` (`/settings/profile*`); the `_Avatar` seam rendering real avatars on
  profile/reviews/feed/comments; the privacy (public/friends-only) toggle.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  npm run build --prefix src/Cinora.Web
  ```
  Tests P1–P6 (§4.4): rename+visibility, avatar persists + renders via the **time-limited URL** (not from
  `wwwroot`), bad-type/oversize rejected, replace deletes the old file, expired/tampered token 404, settings
  require auth. Then `/review-security` (upload + anti-forgery + CSP), `/review-ui`, `/review-code`.

### 4.3 — Watchlist status control on every surface
- **Owner:** backend-agent + database-agent (confirm indexes; none new here) + frontend-agent + ux-agent +
  performance-agent (batch map / N+1).
- **Deliverable:** `SetWatchlistStatusCommand`, `RemoveFromWatchlistCommand`, `GetWatchlistStatusQuery`,
  `GetWatchlistStatusMapQuery`; `WatchlistController` (set/remove + control) with the `EnsureTitleCached`→
  watchlist orchestration; `_WatchlistControl` wired into Details (replacing the stub) and `_TitleCard`
  (rails/search) via the batch map; the Watched→review nudge.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests W1–W6 (§5.6): set-from-card first-touch-persists-then-upserts, idempotent remove, **one-query batch
  map scoped to me**, auth+token required, server-resolved actor, Watched nudge. Then `/review-performance`
  (no N+1 across a rail), `/review-security` (ownership/anti-forgery), `/review-ui`, `/review-code`.

### 4.4 — Watchlist page (filter, sort, keyset) + counts + empty states
- **Owner:** backend-agent + database-agent (indexes) + performance-agent (keyset) + frontend-agent + ux-agent.
- **Deliverable:** `GetMyWatchlistQuery` (keyset, filter, sort) + `GetWatchlistCountsQuery`; `WatchlistItemVm`
  + `WatchlistProjection` + `WatchlistCursor`; the `/watchlist` page + `/watchlist/page` keyset partial;
  filter chips + counts + empty/empty-filter states; nav "Watchlist" link;
  `IX_Watchlists_UserId_AddedAtUtc` + `IX_Watchlists_UserId_Status_AddedAtUtc`.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests WP1–WP5 (§6.4): my-entries keyset no-overlap, filter scopes page+counts, recently-updated re-orders,
  empty/empty-filter states, auth required. Then `/review-performance` (keyset matches the index, counts in
  one query), `/review-ui`, `/review-code`.

### 4.5 — Public profile watchlist highlights (privacy-shaped)
- **Owner:** backend-agent + security-agent (privacy) + frontend-agent + ux-agent.
- **Deliverable:** extend `GetProfileQuery` with privacy-gated `WatchlistHighlights` (+ `CanViewWatchlist`);
  `WatchlistHighlightVm`; the profile poster strip; real avatars everywhere via `_Avatar`.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests PH1–PH3 (§7.1): private profile hides highlights from a non-friend (never fetched), highlights track
  the privacy tier, real avatar renders with monogram fallback. Then `/review-security` (privacy at the
  read), `/review-ui`, `/review-code`.

**End-of-phase gate (Exit Criteria):** `dotnet build Cinora.sln -c Release` clean (TWAE on); `dotnet test`
green across all four test projects (mock-first, LocalDB); **watchlist toggling works from every title
surface with instant HTMX feedback and correct persistence**; **avatar upload validates type/size (magic-byte),
stores under `App_Data/uploads` (local `IFileStorage`, NOT Azure Blob; Azurite optional), and renders via
time-limited URLs**; profile + settings complete and responsive with the **privacy setting respected in
queries** (data-read gate, not template); **CSP unchanged**; the `EnsureTitleCached`→watchlist orchestration
exercised (ADR 0008 seam's Phase-4 consumer); **zero Critical/High** across the five review gates. Run the
full review-gate loop at phase end.

---

_Design authored 2026-07-03 against the Phase-1/2/3 code (verified: `Watchlist.Add`/`ChangeStatus` +
`WatchlistConfiguration` unique `(UserId, MovieId)`; `WatchlistStatus` PlanToWatch/Watching/Watched; `User`
`Rename`/`SetAvatar`/`SetProfileVisibility` + `AvatarFileKey?`/`IsProfilePublic`; `FileStorageOptions`
free-local design (`Local`, `App_Data/uploads`, `/uploads`, 5 MB, jpeg/png/webp); `IAppDbContext` +
`DiscardPendingChanges`; `EnsureTitleCachedCommand`/`GetCachedMovieIdQuery` (ADR 0008); the ADR-0008
controller-orchestration in `ReviewsController.Create`; `GetProfileQuery`/`ProfileVm` privacy tiers +
`ReviewProjection` keyset pattern; `DiscoveryController` conventions incl. the Details watchlist stub;
`ICurrentUser`/`ForbiddenAccessException`→403 (ADR 0009); `SecurityHeadersMiddleware` strict CSP; the
`social-write`/`public-read` rate-limit policies; **no `IFileStorage`/watchlist/settings types exist in
`src/`**). No application code written._
