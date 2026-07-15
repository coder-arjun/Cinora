# Cinora — Friends & Social Overhaul Solution Design (Authoritative)

- **Status:** Accepted (design) — pre-implementation, brainstormed with the user 2026-07-10
- **Date:** 2026-07-10
- **Owner:** architecture-agent (via the master session)
- **Builds on:** [Phase 3 Reviews & Social design](phase-3-social-design.md), [solution structure](solution-structure.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0003 ApplicationUser placement](../adr/0003-applicationuser-identity-placement.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md),
  [0009 Resource ownership & current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md),
  [0013 File storage & avatar serving](../adr/0013-file-storage-and-avatar-serving.md)
- **New decisions to record as ADRs during implementation:** **0022 User-blocking model** (§2), **0023
  Authenticated-only access** (§8). (Not hyperlinked — files are authored in milestone B1/B4 so links don't
  dangle.)

This document is the single source of truth for the **Friends & Social overhaul** — one of three units the
user requested on 2026-07-10:

- **Unit A — PWA image fix** (posters load directly from `image.tmdb.org`). Decided, small, mechanical;
  captured here only as preliminary **Milestone B0** (§16) so the plan starts with it. Root cause confirmed:
  the deployed free host cannot reach `image.tmdb.org` through the same-origin `/tmdb-img` proxy, so every
  poster degrades to the placeholder.
- **Unit B — this document** (blocking, exact-match user search, redesigned find-friends hub, friends
  management, authenticated-only access).
- **Unit C — chat** (1:1 + group + emoji, realtime). **Separate design pass**, sketched only in §17. Builds on
  the blocking model defined here.

> **Design-only.** No application code was written and no build/test was run producing this document. Every
> `Verify` command in §16 is an acceptance check the *implementing* agent must run and show output for.

> **Governing constraints (unchanged; restated):**
> - **Free / local-only (ADR 0004).** No paid service, no Docker. Realtime (Unit C) stays self-hosted SignalR.
> - **Hand-rolled mediator (ADR 0005).** Every `ISender`/mediator reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (xUnit `Assert` +
>   NSubstitute).
> - **Fail-closed contracts (Phase 1, REVIEW_BACKLOG).** Global fallback authZ (every endpoint requires auth
>   unless `[AllowAnonymous]`); global `AutoValidateAntiforgeryToken` (HTMX writes carry the token via the
>   `RequestVerificationToken` header); strict CSP. This overhaul honors them and — importantly — needs **no
>   CSP widening** (§10).
> - **No generic repository; no business rules in handlers; no external DTO into Domain; no `IConfiguration`
>   injected into services (bind Options); no `Html.Raw` on user content.** All reaffirmed.

---

## 1. What this overhaul adds and where every concern lives

Everything is built on the **existing Phase-3 friend/notification machinery** (§7 of the Phase-3 design). The
only new **entity** is `UserBlock` (§2); the only new **port** is `IUserDirectory` (§4). The layering and
dependency rule are unchanged and inviolable (ADR 0001).

| Concern | Layer / location | New? | Notes |
|---|---|---|---|
| **`UserBlock` entity** (directional block) | `Cinora.Domain/Entities/UserBlock.cs` | NEW | `BlockerId`, `BlockedUserId`, `CreatedAtUtc`; factory `Create` rejects self-block. §2. |
| **`UserBlockConfiguration`** | `Cinora.Infrastructure/Persistence/Configurations/UserBlockConfiguration.cs` | NEW | PK `ValueGeneratedNever`; unique `(BlockerId, BlockedUserId)`; index `(BlockedUserId)`; Restrict FKs to `User`. §11. |
| **`IAppDbContext.UserBlocks`** + `CinoraDbContext.UserBlocks` | `Application/Common/Interfaces/IAppDbContext.cs`, `Infrastructure/Persistence/CinoraDbContext.cs` | NEW | `DbSet<UserBlock>`. |
| **Block guard helper** — `AreBlockedEitherWayAsync`, `BlockedOrBlockedByIdsAsync` | `Cinora.Application/Features/Blocks/BlockQueries.cs` (static, mirrors `FriendProjections`) | NEW | Central fail-closed check consumed by search, profile read, friend-request send, feed, and later chat. §3. |
| **Block/unblock verticals** — `BlockUserCommand`, `UnblockUserCommand`, `GetBlockedUsersQuery` | `Cinora.Application/Features/Blocks/` | NEW | Block also removes any `Friend` row + pending request (either direction) atomically; **silent** (no notification). §5. |
| **Cancel outgoing request** — `CancelFriendRequestCommand` | `Cinora.Application/Features/Friends/` | NEW | Requester-only; removes a pending outgoing row; idempotent. §5. |
| **User search** — `SearchUsersQuery` + `UserSearchVm` | `Cinora.Application/Features/Friends/` | NEW | Exact-match username or email; excludes self + any block relationship. §4. |
| **`IUserDirectory` port** + `UserDirectory` adapter | `Application/Common/Interfaces/IUserDirectory.cs`, `Cinora.Infrastructure/Identity/UserDirectory.cs` | NEW | Resolves a **userId from an exact normalized email** without leaking Identity email into Application (email lives only on `ApplicationUser`, ADR 0003). §4. |
| **Profile block state** — extend `GetProfileQuery` + `ProfileRelationship` | `Cinora.Application/Features/Profiles/GetProfileQuery.cs`, `ProfileVm.cs` | EXTEND | Add `BlockedByMe`; owner-blocked-viewer → `NotFoundException`. §7. |
| **Redesigned social hub** — `FriendsController` new actions | `Cinora.Web/Controllers/FriendsController.cs` | EXTEND | search, block, unblock, cancel; tabbed `/friends`. §6. |
| **Find-people UI + repointed empty states** | `Views/Friends/*`, `Views/Home/Index.cshtml`, shared partials | EXTEND | Replace the movie-**Discovery** "find friends" CTAs with the in-app search + existing WhatsApp invite (`_InviteFriends.cshtml`). §6, §12. |
| **Authenticated-only access** — remove `[AllowAnonymous]` from content | `Cinora.Web/Controllers/*`, `Program.cs` review | EXTEND | Keep public only: landing/marketing, auth pages, error/offline, `/health`, static. §8. |
| **`social-write` rate-limit on new writes; a read limit on search** | attributes on the new actions | EXTEND | Reuse the existing `RateLimitingPolicies.SocialWrite`; add a lightweight per-user search limit. §10. |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no business rules in handlers
(invariants stay on `UserBlock`/`Friend`); no `IConfiguration` in services; no Domain/EF type on an HTMX wire;
**no `Html.Raw` on the searched term, a `DisplayName`, or an email** (§14).

---

## 2. Blocking model (record as ADR 0022)

**Decision: a new, separate, directional `UserBlock` entity — NOT a `Blocked` value on `FriendStatus`.**

`FriendStatus` today is `{ Pending, Accepted, Declined }` and `Friend` guards *"only a pending row can
transition"*. Reasons a block does **not** belong on `Friend`:

1. You can block someone you were **never** friends with (straight from search / a profile) — there is no
   `Friend` row to transition.
2. Blocking is **independent** of the friendship lifecycle; overloading `FriendStatus` would break the
   "only-pending-can-transition" invariant and muddy every friend query.
3. A block is **directional and asymmetric** in intent (A blocks B), which the `(RequesterId, AddresseeId)`
   directed pair does not cleanly express for a non-request relationship.

### 2.1 Entity

`UserBlock` (`sealed`, private ctor, EF-friendly — mirrors `Friend.cs`):

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK, `ValueGeneratedNever` (app-assigned, like `Friend`). |
| `BlockerId` | `Guid` | the user who blocked. |
| `BlockedUserId` | `Guid` | the user who is blocked. |
| `CreatedAtUtc` | `DateTime` | stamp. |

- Factory `UserBlock.Create(Guid blockerId, Guid blockedUserId)` — throws `DomainException` when
  `blockerId == blockedUserId` (self-block). No status enum: **existence = blocked; unblock = delete the row.**
- No `Accept`/transition methods — a block has no lifecycle.

### 2.2 What "block" enforces (full cut-off + hide)

When **A blocks B**:

| Surface | Effect on **B** (the blocked) | Effect on **A** (the blocker) |
|---|---|---|
| Existing friendship / pending request | Removed (either direction), atomically in the block's `SaveChanges`. | Removed. |
| User **search** | B searching an exact identifier that resolves to A → **no result** (indistinguishable from "not found" — never reveals the block, §14). | A searching B → **no result** too (mutual hide). |
| **Profile** `/users/{A}` | B → **404 / not available** (`NotFoundException`; B cannot see A's username). | — |
| **Profile** `/users/{B}` | — | A sees a minimal **"You blocked this user — Unblock"** state (`BlockedByMe`), not B's content. |
| Friend **request** | B → A blocked (send rejected). | A → B blocked (must unblock first). |
| **Feed / social lists** | A never appears to B (and vice-versa). | — |
| **Chat** (Unit C) | B cannot open or message a room with A; existing rooms are hidden. | Symmetric. |
| Notification | **Silent** — B is **not** told they were blocked. | — |

The block is **mutual for visibility** (neither sees the other in search/profile/feed) but **one-sided for
control** (only A can unblock; only A sees B in the Blocked tab). This satisfies the user's requirement —
*"blocked friends should not see my username in the app."*

---

## 3. Block enforcement — one central, fail-closed seam

All enforcement routes through **one** helper module so the rule can't be applied inconsistently
(`Cinora.Application/Features/Blocks/BlockQueries.cs`, static, mirroring `FriendProjections`):

- `Task<bool> AreBlockedEitherWayAsync(IAppDbContext db, Guid a, Guid b, CancellationToken ct)` — `true` if a
  `UserBlock` exists `a→b` **or** `b→a`. Backed by the unique index and the `(BlockedUserId)` index.
- `Task<HashSet<Guid>> BlockedOrBlockedByIdsAsync(IAppDbContext db, Guid me, CancellationToken ct)` — the set
  of every user in a block relationship with me (either direction), for **bulk exclusion** in list reads
  (search, feed) with no N+1.

**Consumers (fail-closed — a block always denies/hides):**

1. `SearchUsersQuery` (§4) — excludes any id in `BlockedOrBlockedByIdsAsync`.
2. `GetProfileQuery` (§7) — owner→viewer block ⇒ `NotFoundException`; viewer→owner block ⇒ `BlockedByMe`.
3. `SendFriendRequestCommand` (§5) — `AreBlockedEitherWayAsync` true ⇒ reject (a new `FriendRequestOutcome`,
   e.g. `Blocked`).
4. `GetActivityFeedQuery` — defensive exclusion (a block removes friendship, so blocked users shouldn't be in
   the friend set anyway; the exclusion is belt-and-suspenders).
5. **Unit C chat send / room open** — same helper.

Because block state is small and hot, these are cheap `Any(...)` / set membership checks on indexed columns.

---

## 4. User search (exact match — the user's chosen privacy stance)

**Decision: exact-match only.** You must type the **full username** or the **full email** to find someone.
This prevents scraping/enumerating the user base and is the correct default for email lookup.

### 4.1 Vertical

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `SearchUsersQuery(string Term)` | Query (+validator: non-blank, trimmed, bounded length) | `IAppDbContext`, `ICurrentUser`, `IUserDirectory` | `UserSearchVm` (0 or 1 `UserSearchResultVm`) | Term with `@` → **email path**; else **username path**. Excludes self + `BlockedOrBlockedByIdsAsync`. |

- **Username path:** exact match on `User.NormalizedDisplayName` (normalize the term the same way `User.Normalize` does — upper-invariant). `NormalizedDisplayName` is already **unique-indexed** (the login handle), so this is a seek, and there is at most one match.
- **Email path:** `IUserDirectory.FindUserIdByEmailAsync(term, ct)` → `Guid?`. Email lives on `ApplicationUser` (Identity), which the **Application layer must not reference** (ADR 0003). The port keeps the boundary intact; the Infrastructure adapter matches Identity's `NormalizedEmail` (already indexed) exactly. The returned id is then projected against `db.Users` for `DisplayName`/`AvatarFileKey`/relationship — the same projection as the username path.
- **`UserSearchResultVm`:** `UserId, DisplayName, AvatarFileKey?, Relationship` where `Relationship ∈ { None, RequestOutgoing, RequestIncoming, Friend, BlockedByMe }` (reuses/extends the profile relationship resolution, §7). Drives the card's control (Add friend / Requested / Accept / Friends / Unblock).
- **Privacy shaping:** exact-match returns the user **regardless of `IsProfilePublic`** (the point of exact match is that you already know their handle/email and want to connect) — but the result reveals **only** `DisplayName` + avatar + relationship, never email or activity. A no-match and a blocked/hidden user both return an **empty** result (§14) so the caller can't distinguish "doesn't exist" from "blocked me."

### 4.2 The `IUserDirectory` port

```
// Cinora.Application/Common/Interfaces/IUserDirectory.cs
public interface IUserDirectory
{
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken ct);
}
```

Implemented by `Cinora.Infrastructure/Identity/UserDirectory.cs` (exact `NormalizedEmail` lookup via
`UserManager<ApplicationUser>`/the Identity `DbSet`), registered in `AddInfrastructure`. This is the same
port-shaped seam the codebase already uses for `ICurrentUser`/`IFileStorage` — Application stays ignorant of
Identity types.

---

## 5. Friends-management verticals (block, unblock, cancel, list-blocked)

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `BlockUserCommand(Guid TargetUserId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit` | `UserBlock.Create(me, target)`; **in the same `SaveChanges`**: delete every `Friend` row for the unordered pair (any status, either direction) and any pending request. Idempotent (already blocked → no-op). **Silent** — no notification (§9). Self-block → `DomainException`→400. |
| `UnblockUserCommand(Guid TargetUserId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit` | Delete the `me→target` `UserBlock` if present; idempotent. Does **not** restore friendship (they must re-request). |
| `GetBlockedUsersQuery()` | Query | `IAppDbContext`, `ICurrentUser` | `BlockedUsersVm` (list of `BlockedUserVm { UserId, DisplayName, AvatarFileKey }`) | The users **I** have blocked (`BlockerId == me`), for the Blocked tab. Projected, `AsNoTracking`. |
| `CancelFriendRequestCommand(Guid RequestId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit` | Load the pending `Friend`; **requester-only** (`me == RequesterId`, else `ForbiddenAccessException`→403); must be `Pending`; remove. Idempotent (already gone → treat as success). Closes the current "no cancel outgoing" gap. |

`SendFriendRequestCommand` gains a **block pre-check** (§3.3) and a new `FriendRequestOutcome.Blocked` arm
(mapped to a neutral, non-revealing message — §14).

---

## 6. Redesigned "Find friends" surface (the core UX ask)

Today every "find friends" affordance dead-ends at the movie **Discovery** page (title poster tiles). This
overhaul makes `/friends` the **social hub** and repoints those CTAs.

### 6.1 `/friends` becomes tabbed

`FriendsController.Index` renders `Views/Friends/Index.cshtml` with four clear sections:

- **Find people** — an exact-match search box (`hx-get /friends/search`, debounced, results swapped into a
  region) **+ the existing WhatsApp invite card** (`_InviteFriends.cshtml`, already built — for people not on
  the app). The search-result card shows the person + the correct relationship control.
- **Requests** — Incoming (Accept / Decline) and Outgoing (now with **Cancel**, §5).
- **Friends** — accepted friends (`_FriendCard.cshtml`, Remove).
- **Blocked** — the users I've blocked, each with **Unblock** (`GetBlockedUsersQuery`).

### 6.2 New controller actions (all under `FriendsController`, `[Authorize]`)

| Verb + Route | Dispatches | Returns |
|---|---|---|
| `GET /friends/search?term=` | `SearchUsersQuery` | `_UserSearchResult` partial (a person card or an empty "no matches" state). Rate-limited (§10). |
| `POST /friends/{userId:guid}/block` | `BlockUserCommand` | updated control (profile → `_ProfileFriendControl`; list → row removed). |
| `DELETE /friends/{userId:guid}/block` | `UnblockUserCommand` | updated Blocked-tab row (removed) / control. |
| `POST /friends/requests/{id:guid}/cancel` | `CancelFriendRequestCommand` | `_FriendActionResult` "Request cancelled." |

Existing actions (`Send`, `Accept`, `Decline`, `Remove`) are unchanged.

### 6.3 Repointed entry points

- `Views/Home/Index.cshtml` — the no-friends feed empty state's **"Discover titles"** button → **"Find
  friends"** linking to `/friends` (find-people section).
- `Views/Friends/Index.cshtml` — the empty-friends card that currently links `Discovery/Index` → the
  find-people section (same page).
- Profile pages gain a **Block / Unblock** control beside the friend control (§7).

*(No movie-Discovery functionality is removed — only the mislabeled "find friends → movie tiles" links are
repointed to the real people surface.)*

---

## 7. Profile block control + privacy interaction

`GetProfileQuery` and the `ProfileRelationship` enum (`Owner/Friend/RequestIncoming/RequestOutgoing/None`)
extend to cover blocking:

- Add `ProfileRelationship.BlockedByMe`.
- **Resolution order** in `GetProfileQuery` (before the existing relationship logic):
  1. If **owner blocked the viewer** (`owner→viewer` block) → throw `NotFoundException` (viewer cannot see the
     profile at all — mirrors "hidden"; returns the same 404 as a non-existent user, §14).
  2. If **viewer blocked the owner** (`viewer→owner` block) → return a minimal `ProfileVm` with
     `Relationship = BlockedByMe` (name only + an **Unblock** control; no reviews/activity).
  3. Otherwise the existing Owner/Friend/RequestIncoming/RequestOutgoing/None logic, **plus** a **Block**
     action available in the `None`/`Friend`/`Request*` states (in an overflow/kebab menu).
- `_ProfileFriendControl.cshtml` gains the `BlockedByMe` arm (Unblock) and a Block affordance in the other
  arms. Since a block **removes** friendship, `Friend + Block` collapses to `BlockedByMe`.

This keeps privacy enforced **at the data read**, not the template (consistent with the Phase-3 §7.3 stance).

---

## 8. Authenticated-only access (record as ADR 0023)

**Decision (user, 2026-07-10): require login for everything.** This reverses the Phase-3 §7/§10 stance that
title reviews/comments and the Details/Discovery surfaces are `[AllowAnonymous]`.

- **Mechanism:** the global fail-closed fallback policy already requires auth; making the app private is a
  matter of **removing `[AllowAnonymous]`** from the content controllers/actions. The implementing agent must
  **enumerate every `[AllowAnonymous]`** in `Cinora.Web` and reclassify.
- **Stays public (the allowlist):** the **landing / marketing page** (the entry that offers "Sign in"), the
  **auth pages** (login, register, Google external-login challenge + callback, logout), **error/offline**
  pages, **`/health`**, and **static assets** (`/dist`, `/icons`, manifest, `sw.js`). *(Assumption confirmed
  in brainstorming: keep a public landing → login entry, rather than dumping anonymous visitors straight onto
  the login form.)*
- **Everything else requires auth:** Discovery, Search, Details, reviews/comments reads, profiles, feed,
  friends, notifications, watchlist, recommendations.
- **Consequences to fix up:** the Details page's "Sign in to review" anonymous branch and any anonymous
  output-cache variant (Phase-3 §13) become dead and are removed; the public reviews/comments partials are now
  authed reads.
- **Interaction with Unit A:** posters now load client-side directly from `image.tmdb.org`, and only
  authenticated users ever see them — no conflict; CSP already allows the host (no widening, §10).

---

## 9. Notifications

- **Friend request / accept** notifications are **unchanged** (Phase-3 §9.1 — co-persisted, best-effort
  realtime + Web Push via `RealtimeNotificationDispatcher`; deep-links `/friends` and `/users/{actor}`).
- **Blocking is silent** — no notification of any kind to the blocked user (§2.2). Unblocking is silent too.
- **Cancelling** an outgoing request sends no notification (it never notified on cancel; the original request
  notification, if unread, deep-links to `/friends` where the request will simply be gone — acceptable).
- No new `NotificationType` values are introduced.

---

## 10. Anti-forgery / authZ / CSP / rate-limit contract

Every new endpoint honors the standing global contracts (REVIEW_BACKLOG):

1. **Fail-closed authZ.** `FriendsController`/`ProfilesController` are `[Authorize]`; the new search/block/
   unblock/cancel actions inherit it. After §8, there are **no** `[AllowAnonymous]` content reads.
2. **Anti-forgery.** Every state-changing POST/DELETE (block, unblock, cancel — plus existing send/accept/
   decline/remove) carries the token via the already-wired `RequestVerificationToken` header; the global
   `AutoValidateAntiforgeryToken` validates it. No new wiring; no opt-out.
3. **CSP — NO widening.** Search results, block controls, and the tabbed hub are server-rendered Razor
   partials swapped by HTMX (`connect-src 'self'` covers the same-origin `hx-get`/`hx-post`); no inline
   script, no new host. **`img-src` already includes `https://image.tmdb.org`** (Unit A relies on this — it is
   *already present*, so even Unit A needs no CSP change). The CSP SHA-256 stays byte-unchanged.
4. **Rate limiting.** Block/unblock/cancel POSTs go under the existing `RateLimitingPolicies.SocialWrite`
   per-user policy. **`GET /friends/search`** gets a lightweight per-user search limit (a new policy or a reuse
   of `SocialWrite`) to blunt exact-match probing/enumeration (§14). Relaxed in Dev/Testing like the others.

---

## 11. Migrations / schema deltas (for database-agent)

**One new table, additive, low-risk.**

| Migration | Adds | Details |
|---|---|---|
| `AddUserBlock` | table `UserBlocks` | `Id` (PK, no identity), `BlockerId`, `BlockedUserId`, `CreatedAtUtc`; **unique index `(BlockerId, BlockedUserId)`**; **index `(BlockedUserId)`** (reverse "does X block me?" + bulk exclusion); two `HasOne<User>().WithMany().HasForeignKey(...)` with **`DeleteBehavior.Restrict`** (avoid SQL Server multiple-cascade-paths, exactly like `FriendConfiguration`). |

- **No column changes** to existing entities. Search reuses the **existing** unique index on
  `User.NormalizedDisplayName` and Identity's existing `NormalizedEmail` index — **no new index** for search.
- Generated in `Cinora.Infrastructure`, startup `Cinora.Web`; apply via the reviewed **idempotent SQL script**
  (`artifacts/migrate.sql`) / `db-update.ps1` (the app never auto-migrates). This is the **first migration
  since Phase 6**, additive-only.
- **Deployment note:** this migration must be applied to the remote **`db58983`** SQL Server (MonsterASP.NET)
  as part of shipping Unit B — see §16 and `docs/deployment/monsterasp-filezilla-deploy.md`.

---

## 12. UI screens

(frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`, `alpine-htmx-interactivity`,
`responsive-accessibility`, `ui-animations`.)

- **`/friends` (the hub):** a tabbed/sectioned layout — **Find people** (search box + WhatsApp invite card),
  **Requests** (incoming Accept/Decline, outgoing Cancel), **Friends** (Remove), **Blocked** (Unblock). Search
  is `hx-get` with a debounced input and an `aria-live` results region; each result is a person card
  (`_Avatar` partial + `DisplayName` + relationship control). Empty/"no matches" state is a calm message, not
  an error.
- **Profile page:** add a **Block** control (kebab/overflow menu) in the None/Friend/Request states and an
  **Unblock** control in the `BlockedByMe` state; blocked-by-owner renders the standard 404/not-available page.
- **Repointed empty states** (§6.3): Home feed + Friends page "find friends" CTAs point at the hub's
  find-people section.
- **Encoding:** the searched term, every `DisplayName`, and any email are **Razor-encoded** — never
  `Html.Raw`, never echoed raw into markup or an HTMX attribute (§14).
- **Accessibility:** search input labelled; results `aria-live="polite"`; Block/Unblock are real buttons with
  discernible names; the destructive **Block** uses the existing CSP-safe styled confirm pattern (Phase 6.5),
  not a native `confirm()`.

**CSP/contract extensions: none** (§10) — a deliberate, load-bearing property.

---

## 13. Performance stance

- **Search is a single indexed seek** (unique `NormalizedDisplayName`, or Identity `NormalizedEmail`), never a
  scan/`LIKE '%term%'` — exact-match is both the privacy stance *and* the fast path.
- **Block checks** are `Any(...)`/set-membership on indexed `(BlockerId, BlockedUserId)` / `(BlockedUserId)`;
  list reads use the **bulk** `BlockedOrBlockedByIdsAsync` set to avoid N+1.
- **`AsNoTracking().Select(...)`** projections for every read (search, blocked list); writes load a tracked
  entity → domain method → `SaveChanges`, with the block's cascade-cleanup batched into one save.
- **Async end-to-end**, `CancellationToken` threaded controller → `ISender` → handler → EF.

---

## 14. Security stance (privacy-critical)

- **No enumeration signal.** Search is **exact-match** and **rate-limited** (§10). A no-match, a
  blocked-either-way user, and a private user searched by a *wrong* identifier all return the **same empty
  result** — the response must not let a caller distinguish "doesn't exist" from "blocked me" from "exists but
  hidden." (Email exact-match still confirms existence for a *correct* full address; that residual is accepted
  given auth-required + rate-limit + the user's explicit exact-match choice.)
- **Block is fail-closed** (§3): any `UserBlock` in either direction denies requests and hides profiles/search
  results; the check is centralized so it can't be forgotten on a new surface.
- **XSS:** `DisplayName` and the echoed search term are user-controlled → **Razor output-encoding only**, no
  `Html.Raw`, no client-side `innerHTML = userString`. Search results are server-rendered partials.
- **Anti-forgery + authZ** on every new write (§10). Block/unblock/cancel are ownership-scoped in the handler
  (actor from `ICurrentUser`, never a bound field).
- **Authenticated-only access** (§8) removes the anonymous attack surface on content entirely.

---

## 15. Risks and product decisions

**Risks baked into the design:**
- **Block race:** two concurrent `BlockUserCommand`s or a block racing a friend-accept — the unique index
  prevents duplicate blocks; the block's `SaveChanges` removes friendship last-writer-wins; a rare interleave
  is reconcilable (idempotent block, defensive feed exclusion). Acceptable for the local/free profile.
- **Email existence residual** (§14) — exact email match confirms an address exists. Mitigated, accepted.
- **`[AllowAnonymous]` enumeration completeness (§8):** the lockdown is only as good as the audit — the
  implementer must grep **every** `[AllowAnonymous]` and justify each survivor against the allowlist; an
  integration test asserts anonymous → 302-login on representative content routes.
- **Relationship-resolution duplication:** search and profile both compute a viewer→target relationship —
  share one helper (extend the existing `ResolveRelationshipAsync`) to stay DRY.

**Genuine PRODUCT / UX decisions (locked with the user 2026-07-10 unless noted):**
- **Blocking = full cut-off + hide** (§2.2). ✅ locked.
- **Search = exact-match only**, username or email (§4). ✅ locked.
- **Require login for everything**, public landing + auth kept (§8). ✅ locked.
- **Findable while `IsProfilePublic = false`?** Default **yes** by exact identifier (the point of exact match);
  content stays private until friends. *Flagged — revertible if the user wants private users unfindable.*
- **Re-request after unblock:** the other party must send a fresh request (unblock does not restore
  friendship). Default; flagged.
- **Search debounce vs button:** UX default (debounced `hx-get`); overridable.

---

## 16. Milestone build order (delegable)

Ordered for the orchestrator. Each states owning agent(s), deliverable, exact `Verify` acceptance, and the
review gates (`/review-architecture`, `/review-code`, `/review-security`, `/review-performance`, `/review-ui`)
that run automatically after every milestone and at the end (per `CLAUDE.md`). All commands run from the repo
root. Integration tests run against real **LocalDB `CinoraTest`**.

> **Prerequisite reality check (fold into B1):** confirm `ICurrentUser`, `FriendProjections`,
> `GlobalExceptionHandler` (403/404 arms), and `RateLimitingPolicies.SocialWrite` exist as the Phase-3 design
> describes before building on them.

### B0 — Unit A: posters load directly from TMDB (preliminary, small)
- **Owner:** frontend-agent (tag helper) + backend-agent (retire the proxy path) + security-agent (confirm CSP
  unchanged).
- **Deliverable:** `TmdbImageTagHelper` emits absolute `https://image.tmdb.org/...` URLs (reuse the existing
  `ITmdbImageUrlBuilder`, which already builds them); retire/neutralize `TmdbImageController` (`/tmdb-img`) and
  the now-dead service-worker poster-proxy branch (the SW's `image.tmdb.org` cache-first path becomes live
  again — a plus). No CSP change (`img-src` already allows the host).
- **Verify:**
  ```
  dotnet build Cinora.sln -c Release
  npm run build --prefix src/Cinora.Web
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Build clean (TWAE on); a view/tag-helper test asserts a poster `src` now points at `image.tmdb.org`; CSP
  SHA-256 unchanged. **Manual (deployed):** posters render on the live MonsterASP.NET app. Then
  `/review-security` (CSP), `/review-code`.

### B1 — Blocking foundation
- **Owner:** database-agent (entity/config/migration) + backend-agent (verticals + guard + enforcement) +
  security-agent (fail-closed) + frontend-agent (profile Block/Unblock + Blocked tab) + architecture-agent
  sign-off (ADR 0022).
- **Deliverable:** `UserBlock` entity + `UserBlockConfiguration` + `IAppDbContext.UserBlocks` + the
  `AddUserBlock` migration; `BlockQueries` helper; `BlockUserCommand`/`UnblockUserCommand`/`GetBlockedUsersQuery`;
  block enforcement in `GetProfileQuery` (+`BlockedByMe`) and `SendFriendRequestCommand` (+`Blocked` outcome);
  profile Block/Unblock control + the Blocked tab; ADR 0022.
- **Verify:**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Domain.Tests/Cinora.Domain.Tests.csproj
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests green: self-block rejected; block removes friendship + pending both directions; blocked user
  → 404 on the blocker's profile; blocker sees Unblock; send-request-while-blocked → rejected/neutral;
  unblock is idempotent and does not restore friendship. Then `/review-security` (fail-closed + no leak),
  `/review-code`, `/review-ui`.

### B2 — User search (exact match) + `IUserDirectory`
- **Owner:** backend-agent (query + port) + security-agent (enumeration/rate-limit) + frontend-agent (search
  UI).
- **Deliverable:** `IUserDirectory` port + `UserDirectory` adapter (exact `NormalizedEmail`); `SearchUsersQuery`
  + `UserSearchVm` (username exact + email exact, self/block exclusion, empty-on-no-match); `GET
  /friends/search` + `_UserSearchResult` partial + a per-user search rate-limit.
- **Verify:**
  ```
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests: exact username match returns the one user; exact email match resolves via the port; partial term →
  **no** match; a blocked-either-way user → empty (indistinguishable from not-found); self excluded; searched
  `<script>` term rendered encoded. Then `/review-security`, `/review-code`, `/review-ui`.

### B3 — Social hub redesign + cancel outgoing
- **Owner:** backend-agent (`CancelFriendRequestCommand`) + frontend-agent + ux-agent (tabbed hub, WhatsApp
  invite placement, repointed empty states).
- **Deliverable:** `CancelFriendRequestCommand` + `POST /friends/requests/{id}/cancel`; the tabbed
  `Views/Friends/Index.cshtml` (Find people / Requests / Friends / Blocked); repoint the Home-feed and Friends
  empty-state CTAs from movie Discovery → the find-people section; surface the existing `_InviteFriends`
  WhatsApp card in Find people.
- **Verify:**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  npm run build --prefix src/Cinora.Web
  ```
  Tests: requester can cancel a pending outgoing (403 for a non-requester); the hub renders all four sections;
  the repointed CTAs link to `/friends`. Then `/review-ui`, `/review-code`.

### B4 — Authenticated-only access (lockdown)
- **Owner:** security-agent (audit) + backend-agent (remove `[AllowAnonymous]`, prune dead anonymous branches)
  + architecture-agent sign-off (ADR 0023).
- **Deliverable:** every `[AllowAnonymous]` on content removed; the public allowlist (landing/auth/error/
  offline/health/static) documented and asserted; the Details "sign in to review" anonymous branch + any
  anonymous output-cache variant removed; ADR 0023.
- **Verify:**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests: anonymous `GET /discover`, `/discover/title/...`, `/users/{id}`, `/watchlist`, `/recommendations` →
  **302 login**; anonymous `GET /` (landing), `/health`, login/register still **200**. Then `/review-security`
  (lockdown completeness), `/review-code`.

### End-of-unit gate (Exit Criteria)
`dotnet build Cinora.sln -c Release` clean (TWAE on); `dotnet test` green across all four test projects;
blocking (full cut-off + hide), exact-match search, the redesigned hub with WhatsApp invite + in-app requests,
cancel-outgoing, and authenticated-only access all functioning; **one additive migration** (`AddUserBlock`);
**CSP byte-unchanged**; **zero Critical/High** across the five review gates. Then the deployment step (Unit A +
B shipped together to MonsterASP.NET, migration applied to `db58983`) per the agreed deploy mode.

---

## 17. Relationship to Unit C — chat (separate design pass)

Chat (1:1 + group + emoji, realtime) was **deferred** on this project (ADR 0010) and is now **explicitly opted
in** by the user (2026-07-10). It is **out of scope for this document** and gets its own design, which will
build directly on:

- the **blocking model** here (a blocked pair cannot share or open a room — §3 consumer #5);
- the **exact-match user search** (starting a 1:1 or adding a group member reuses `SearchUsersQuery` / friend
  selection);
- the Phase-3 **self-hosted SignalR** foundation (ADR 0011), which was deliberately built chat-ready.

Chat's own spec will settle: `Conversation`/`Message`/`ConversationMember` entities + migration, a `ChatHub`
(participants-only groups, fail-closed auth), the CQRS verticals (start/send/history-keyset/read), plain-text
Razor-encoded messages + an emoji picker (Unicode text, no new CSP host), group create/add/leave, a send
rate-limit, and offline delivery via the existing Web Push. **Do not build chat from this document.**

---

_Design authored 2026-07-10 against the live Phase-1–6 code (verified via codebase exploration: `Friend` +
`FriendStatus` {Pending/Accepted/Declined}, `FriendConfiguration` indexes + Restrict FKs, the
`Cinora.Application/Features/Friends/*` verticals, `FriendsController` routes, `ProfilesController`
`GET /users/{id:guid}`, `ProfileRelationship` enum, `NotificationType` {FriendRequest, FriendAccepted, …},
`RealtimeNotificationDispatcher`, `User.NormalizedDisplayName` unique index, `ApplicationUser` Identity email,
`SecurityHeadersMiddleware` CSP with `img-src … https://image.tmdb.org`, the `/tmdb-img` proxy + `sw.ts`,
`_InviteFriends.cshtml` WhatsApp card; confirmed **no** `UserBlock`, **no** user search, **no** cancel-outgoing
exist today). No application code written._
