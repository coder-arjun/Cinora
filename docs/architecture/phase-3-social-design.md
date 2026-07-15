# Cinora — Phase 3 Reviews & Social Solution Design (Authoritative)

- **Status:** Accepted for Phase 3 (Reviews & Social) — pre-implementation design review
- **Date:** 2026-07-03
- **Owner:** architecture-agent
- **Builds on:** [Phase 1 solution structure](solution-structure.md), [Phase 2 Discovery design](phase-2-discovery-design.md)
- **New ADRs:** [0009 Resource ownership & the current-user seam](../adr/0009-resource-ownership-and-current-user-seam.md),
  [0010 In-app chat deferred](../adr/0010-in-app-chat-deferred.md),
  [0011 Self-hosted SignalR](../adr/0011-self-hosted-signalr-realtime.md),
  [0012 Activity feed read-time query](../adr/0012-activity-feed-read-time-query.md)
- **Governing ADRs:** [0001 Layering](../adr/0001-clean-architecture-layering.md),
  [0003 ApplicationUser placement](../adr/0003-applicationuser-identity-placement.md),
  [0004 Free/local-only](../adr/0004-free-local-only-infrastructure.md),
  [0005 Hand-rolled mediator](../adr/0005-hand-rolled-mediator.md),
  [0008 Catalog persistence via commands](../adr/0008-tmdb-persistence-via-commands.md)

This document is the single source of truth for Phase 3 (Reviews & Social). It designs the first authenticated
write use cases, the authorization model, the XSS stance, the `ISender` feature verticals for all five
milestones, the activity feed, self-hosted realtime, the UI, migrations, and performance stance, then gives a
delegable milestone build order. Implementation agents follow it; deviations require an ADR.

> **Design-only.** No code was written and no build/test was run producing this document. Every `Verify`
> command in §16 is an acceptance check the *implementing* agent must run and show output for.

> **Governing constraints (unchanged; restated because Phase 3 is the first with authenticated writes and
> realtime):**
> - **Free / local-only (ADR 0004).** Realtime is **self-hosted SignalR, single instance, no backplane, no
>   Azure SignalR, no Docker** (ADR 0011). No paid services. Ports stay so a paid/scaled provider *could* be
>   swapped later; only the free adapter ships.
> - **Hand-rolled mediator (ADR 0005).** Every "`ISender`/mediator" reference is the in-house type in
>   `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (tests use xUnit
>   `Assert` + NSubstitute).
> - **Fail-closed contracts (Phase 1, REVIEW_BACKLOG).** Global fallback authZ (every endpoint requires auth
>   unless `[AllowAnonymous]`); global `AutoValidateAntiforgeryToken` (HTMX writes carry the token via the
>   `RequestVerificationToken` header); strict CSP. Phase 3 honors these and — importantly — needs **no CSP
>   widening** (§10).
> - **No generic repository; no business rules in handlers; no external DTO into Domain; no `IConfiguration`
>   injected into services (bind Options).** All reaffirmed.

---

## 1. What Phase 3 adds and where every concern lives

Phase 3 introduces the **first authenticated write verticals**, a **current-user seam**, **resource-based
authorization**, and **self-hosted realtime**. Every Phase-3 feature is built on the **existing Phase-1
entities** (`Review`, `ReviewLike`, `Comment`, `Friend`, `Notification`, `Device`, `User`, `Movie`) — **no new
entity is created** (§11). The layering and dependency rule are unchanged and inviolable (ADR 0001).

| Concern | Layer / location | Notes |
|---|---|---|
| **`ICurrentUser` port** (`Guid? UserId`, `bool IsAuthenticated`, `Guid GetRequiredUserId()`) | `Cinora.Application/Common/Interfaces/ICurrentUser.cs` | NEW. Server-resolved actor for every write. Never a client-bound field (ADR 0009). |
| **`CurrentUser` adapter** (reads `IHttpContextAccessor` → `NameIdentifier`) | `Cinora.Infrastructure/Identity/CurrentUser.cs` | Registered in `AddInfrastructure` with `AddHttpContextAccessor()`. Same claim SignalR uses (ADR 0011). |
| **`ForbiddenAccessException`** | `Cinora.Application/Common/Exceptions/ForbiddenAccessException.cs` | NEW. Thrown by ownership checks; mapped to **403** by `GlobalExceptionHandler` (§2, ADR 0009). |
| **`IRealtimeNotifier` port** + `NotificationDto` | `Cinora.Application/Common/Interfaces/IRealtimeNotifier.cs`, `Cinora.Application/Common/Realtime/NotificationDto.cs` | NEW. Push abstraction; DTO on the wire, never a Domain entity (ADR 0011). |
| **Review verticals** — `CreateReviewCommand`, `EditReviewCommand`, `DeleteReviewCommand`, `GetTitleReviewsQuery`, `GetMyReviewForTitleQuery` | `Cinora.Application/Features/Reviews/` | Request + handler + validator + VM colocated. Deps: `IAppDbContext` + `ICurrentUser` (writes). §5. |
| **Like/Comment verticals** — `LikeReviewCommand`, `UnlikeReviewCommand`, `AddCommentCommand`, `DeleteCommentCommand`, `GetReviewCommentsQuery` | `Cinora.Application/Features/Reviews/` (likes), `.../Features/Comments/` | Idempotent like via composite key; flat comments. Notification co-persist (§6, §9). |
| **Friend/Profile verticals** — `SendFriendRequestCommand`, `RespondToFriendRequestCommand`, `RemoveFriendCommand`, `GetProfileQuery`, `GetFriendsQuery`, `GetPendingRequestsQuery` | `Cinora.Application/Features/Friends/`, `.../Features/Profiles/` | Privacy-respecting reads; directed-pair guard in the handler (§7). |
| **Feed vertical** — `GetActivityFeedQuery` (+ `FeedCursor`) | `Cinora.Application/Features/Feed/` | Keyset over friends' reviews (ADR 0012). §8. |
| **Notification verticals** — `GetNotificationsQuery`, `GetUnreadCountQuery`, `MarkNotificationReadCommand`, `MarkAllNotificationsReadCommand` | `Cinora.Application/Features/Notifications/` | Recipient = current user; ownership implicit. §9. |
| **`NotificationHub` + `INotificationClient`** | `Cinora.Web/Hubs/NotificationHub.cs` | `[Authorize]` (fail-closed), group-per-user. Maps `/hubs/notifications`. |
| **`SignalRRealtimeNotifier : IRealtimeNotifier`** | `Cinora.Web/Infrastructure/SignalRRealtimeNotifier.cs` | Adapter over `IHubContext<NotificationHub, INotificationClient>`; registered in `Program.cs` (ADR 0011 §3). |
| **Write controllers** — `ReviewsController`, `CommentsController`, `FriendsController`, `ProfilesController`, `NotificationsController` | `Cinora.Web/Controllers` | `[Authorize]`; inject `ISender` + `ICurrentUser`. Return HTMX partials/redirects. §12. |
| **Public review reads on the Details page** | `Cinora.Web/Controllers/DiscoveryController.cs` (extend) | The title's reviews list is a **public** read on the `[AllowAnonymous]` Details page (§5.4, §12). |
| **Feed shell** — `/home` repurposed | `Cinora.Web/Controllers/HomeController.cs` (`Index` → feed shell) | `[Authorize]` already. Phase-3 feed replaces the placeholder (ADR 0012). |
| **Realtime + write TS** (`@microsoft/signalr` client, like/comment/friend Alpine glue) | `Cinora.Web/Scripts/` (bundled, no CDN) | New free npm dep `@microsoft/signalr`; esbuild-bundled → `script-src 'self'` unaffected. §10. |
| **DI wiring** — `ICurrentUser`, `IHttpContextAccessor`, `AddSignalR()`, `IRealtimeNotifier`, hub mapping | `AddInfrastructure` (`CurrentUser`), `Program.cs` (SignalR, notifier, `MapHub`) | §10. |

**Anti-patterns still banned (reaffirmed):** no generic `IRepository<T>`; no business rules in handlers/
behaviors (invariants stay on entities — `Review`, `Friend`, etc. already enforce them); no `IConfiguration`
in services; no Domain entity or EF type on a realtime/HTMX wire (map to VMs/DTOs); **no `Html.Raw` on user
content** (§3).

---

## 2. Authorization model (ADR 0009)

Phase 3 is the first phase where a request acts **as** a user and **on** a user-owned resource. The model:

- **Actor identity is server-resolved.** Handlers get the current user's `Guid` from **`ICurrentUser`**
  (`GetRequiredUserId()`), never from a bound command field. A hidden `UserId`/`AuthorId` form field is a
  spoofing vector and is **banned** (`security-hardening` rule). Commands carry *resource* ids (route
  `ReviewId`, `MovieId`, `AddresseeUserId`) only.
- **Ownership is enforced in the command handler**, inside the unit of work: load the tracked entity →
  compare its owner to `ICurrentUser.GetRequiredUserId()` → on mismatch throw
  **`ForbiddenAccessException`**. `GlobalExceptionHandler` maps it to **403 ProblemDetails** (new arm,
  alongside `ValidationException`→400 / `NotFoundException`→404 / `DomainException`→400 / else 500). Missing
  resource → `NotFoundException`→404; existing-but-not-yours → 403 (reviews are public, so 403 vs 404 leaks
  nothing).
- **Resource-based `IAuthorizationHandler` is optional presentation sugar** (gating a GET edit-form, hiding
  edit/delete buttons) — **never the sole gate**. The POST/PUT/DELETE always re-checks in the handler.
- **Fail-closed authZ:** all write controllers are `[Authorize]` (or inherit the global fallback policy).
  The only `[AllowAnonymous]` reads in Phase 3 are the **public title-reviews list / comments list** rendered
  on the public Details page. **Profiles, feed, friends, notifications require authentication** (§7 flags the
  one product choice — anonymous access to *public* profiles is deferred).

Ownership map (who may mutate what):

| Resource | Create as | Edit/Delete allowed to | Enforcement |
|---|---|---|---|
| `Review` | current user (one per title) | the review's `UserId` only | `EditReviewCommand`/`DeleteReviewCommand` handler check → 403 |
| `Comment` | current user | the comment's `UserId` only (review-author moderation deferred, §6) | `DeleteCommentCommand` handler check → 403 |
| `ReviewLike` | current user | n/a (toggle by current user) | keyed by `(ReviewId, current UserId)` — you can only (un)like as yourself |
| `Friend` respond | n/a | the request's `AddresseeId` only | `RespondToFriendRequestCommand` handler check → 403 |
| `Notification` read | system | the `RecipientUserId` only | queries/commands filter by `ICurrentUser`; a non-owned id → `NotFound` |

---

## 3. XSS / user-generated content stance

Phase 3 renders the first free-text user content (review bodies, comments, display names). The rule is simple
and absolute:

- **All bodies are PLAIN TEXT. No rich text, no markdown, no HTML in Phase 3.** The single defense is
  **Razor's automatic HTML output-encoding** (`@Model.Body`, `@user.DisplayName`) which encodes on write.
- **`@Html.Raw`, `IHtmlContent` built from user text, `MvcHtmlString`, `[AllowHtml]`, and disabling
  `HtmlEncoder` on any user-derived value are BANNED.** (`security-hardening`: "Razor output-encodes by
  default; never use `Html.Raw` on user content.") This is a review-gate check for every review/comment/
  profile render.
- **No server-side HTML sanitizer library is introduced** (no `HtmlSanitizer` dependency). It would be a new
  dependency to protect against HTML we never render — YAGNI. Encoding at output is the correct boundary.
- **Line breaks** in bodies are presented with CSS `white-space: pre-wrap` on the text container, **not** by
  string-replacing `\n` → `<br>` (which would require raw HTML). Long tokens wrap with `overflow-wrap`.
- **No auto-linking of URLs** in user text in Phase 3 (turning text into `<a>` is an injection/again-raw-HTML
  surface). Deferred; if ever wanted, it is a scoped decision with its own sanitization review.
- **Validation is defense-in-depth, not the XSS boundary.** FluentValidation bounds length
  (`Review.BodyMaxLength = 4000`, `Comment.BodyMaxLength = 2000`) and rejects blank; it does **not** try to
  "filter dangerous input" (encoding, not filtering, is the defense).
- **HTMX-swapped fragments are server-rendered Razor partials**, so swapped-in review/comment cards are
  encoded by the same Razor pipeline — no client-side templating of user strings (no `innerHTML =
  userString`).
- **CSP is the backstop:** `script-src 'self'` (no inline, no eval — already in place from Phase 1/2) means an
  encoding miss cannot execute an injected inline script. Phase 3 does **not** weaken it (§10).

This stance is enforcement of an existing platform standard (Razor default + the security skill), not a new
architectural decision, so it carries no ADR — but it is a mandatory §16 review-gate check.

---

## 4. The current-user seam and the `EnsureTitleCached` reuse (glue for all writes)

- **`ICurrentUser`** (NEW, §1, ADR 0009): the single seam every write handler uses for the actor. Built
  **first** (Milestone 3.1 prerequisite) with `AddHttpContextAccessor()`. Integration tests already run under
  a real Identity cookie, so `NameIdentifier` is present; unit tests fake `ICurrentUser`.
- **`EnsureTitleCachedCommand` (ADR 0008) is now consumed for real.** Creating a review from the public
  Details page needs the internal `Movie.Id`, but the page addresses titles by `(media, tmdbId)`. The
  **controller orchestrates** (ADR 0008's pattern — never `ISender` inside a handler):
  1. `var ensure = await sender.Send(new EnsureTitleCachedCommand(tmdbId, media), ct);`
  2. if `ensure.Outcome == NotFound` → 404 (cannot review a title TMDB does not have);
  3. else `await sender.Send(new CreateReviewCommand(ensure.MovieId!.Value, rating, body), ct);`

  This is the **first real consumer** of the ADR-0008 seam and validates its "controller orchestration"
  contract. `CreateReviewCommand` therefore carries the internal `Guid MovieId`, not the TMDB id.

---

## 5. Milestone 3.1 — Reviews CRUD (contract)

**Scope:** create/edit/delete a review (rating 1–10 + text) on a title; a title's public reviews list
(paginated); the current user's own review surfaced on the Details page. Resource-based authZ (own-only
edit/delete). XSS-safe rendering.

### 5.1 Verticals (`Cinora.Application/Features/Reviews/`)

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `CreateReviewCommand(Guid MovieId, int Rating, string Body)` | Command (+validator) | `IAppDbContext`, `ICurrentUser` | `CreateReviewResult(Guid ReviewId)` | `Review.Create(currentUser, MovieId, Rating.From(Rating), Body)`. Unique `(UserId, MovieId)` race → catch `DbUpdateException`, re-probe, return a friendly "already reviewed" (`ValidationException` or a dedicated outcome). |
| `EditReviewCommand(Guid ReviewId, int Rating, string Body)` | Command (+validator) | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load tracked; **ownership check → 403**; `ChangeRating` + `EditBody`; `SaveChanges`. |
| `DeleteReviewCommand(Guid ReviewId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load tracked; **ownership check → 403**; remove; `SaveChanges`. FK cascade removes its `ReviewLike`s + `Comment`s. |
| `GetTitleReviewsQuery(Guid MovieId, ReviewCursor? Cursor, int Take = 10)` | Query | `IAppDbContext`, `ICurrentUser` (optional — for `LikedByMe`) | `TitleReviewsVm` (page of `ReviewVm` + cursor) | **Public** read. Keyset `(CreatedAtUtc DESC, Id DESC)`. Projects author `DisplayName`, `LikeCount`, `CommentCount`, and — when authed — `LikedByMe`. |
| `GetMyReviewForTitleQuery(Guid MovieId)` | Query | `IAppDbContext`, `ICurrentUser` | `ReviewVm?` | Drives create-vs-edit on the Details page; `null` when the user has no review or is anonymous. **(As built: returns the shared `ReviewVm?` rather than a near-identical `MyReviewVm`, reusing `_ReviewCard` — DRY; 3.1 impl decision.)** |

- **Rating invariant stays in the domain.** The validator checks `1..10` for a friendly 400, but the
  authoritative guard is `Rating.From(int)` / `Review.Create`/`ChangeRating` (they throw `DomainException` →
  400). Never re-implement the rule in the handler.
- **`ReviewVm`** (Application record): `ReviewId, AuthorUserId, AuthorDisplayName, AuthorAvatarFileKey?,
  Rating (int), Body, CreatedAtUtc, UpdatedAtUtc?, LikeCount, CommentCount, LikedByMe, IsMine`. No Domain
  entity, no EF type. `IsMine`/`LikedByMe` drive the UI's edit/like affordances (view sugar; the server
  re-checks on write).
- **Counts without N+1:** the list projection computes `LikeCount`/`CommentCount` as correlated subqueries in
  one query (`Reviews.Select(r => new ReviewVm { ..., LikeCount = ctx.ReviewLikes.Count(l => l.ReviewId ==
  r.Id), ... })`), `AsNoTracking()`. `LikedByMe` is a bounded `Any(...)` with the current user id (or a set
  membership from a single pre-loaded id list) — never a per-row round-trip.

### 5.2 Controllers & routes

- **`ReviewsController` `[Authorize]`** (writes; `ISender`, `ICurrentUser`):
  - `POST /reviews` `{ TmdbId, Media, Rating, Body }` → orchestrate `EnsureTitleCachedCommand` → `CreateReviewCommand` (§4); returns the rendered `_ReviewCard` partial to swap into the Details reviews region (HTMX), or a redirect for no-JS.
  - `GET /reviews/{id:guid}/edit` → `_ReviewEditForm` partial (optional resource policy gate; the handler re-checks anyway).
  - `POST /reviews/{id:guid}` (edit) → `EditReviewCommand`; returns the updated `_ReviewCard`.
  - `DELETE /reviews/{id:guid}` → `DeleteReviewCommand`; returns an empty 200 for HTMX to remove the node.
- **Public reviews list** stays on **`DiscoveryController`** (the `[AllowAnonymous]` Details surface):
  `GET /discover/title/{media}/{tmdbId:int}/reviews?cursor=` → `_ReviewList` partial (load-more sentinel,
  reusing the 2.4 keyset/sentinel grammar). The Details view (2.5) embeds the first page + the write form (or
  a "sign in to review" prompt when anonymous).

### 5.3 Anti-forgery & fail-closed obligations

Every write above is a POST/PUT/DELETE on an `[Authorize]` controller: it **requires auth** and **carries the
anti-forgery token** via the already-wired `RequestVerificationToken` header (HTMX) — no new wiring. `DELETE`
via `hx-delete` is validated by the global `AutoValidateAntiforgeryToken` like any unsafe verb.

### 5.4 Test plan (3.1)

| # | Test | Asserts |
|---|---|---|
| R1 | `Create_review_persists_and_stamps_current_user` | POST as user A creates a `Review` with `UserId = A`; a hidden `UserId=B` field is ignored (actor is server-resolved). |
| R2 | `Second_review_for_same_title_is_rejected` | A second `CreateReviewCommand` for the same `(user, movie)` does not create a duplicate (unique index) and returns the friendly already-reviewed result. |
| R3 | `Edit_others_review_is_403` | User B `POST /reviews/{A's id}` → **403** ProblemDetails; the row is unchanged. |
| R4 | `Delete_own_review_cascades_likes_and_comments` | A deletes their review → row gone; its `ReviewLike`s and `Comment`s gone (FK cascade). |
| R5 | `Title_reviews_list_is_public_and_keyset_paginated` | Anonymous `GET /discover/title/movie/{id}/reviews` → 200 with encoded bodies; a second page via the cursor returns the next slice with no overlap. |
| R6 | `Review_body_is_html_encoded` | A body containing `<script>` renders encoded (no executable markup); asserts no `Html.Raw` path. |
| R7 | `Rating_out_of_range_is_400` | `Rating = 0`/`11` → 400 (validator and/or `DomainException`). |

**Migration (database-agent):** `IX_Reviews_MovieId_CreatedAtUtc` on `(MovieId, CreatedAtUtc)` for the
title-reviews keyset (existing indexes: unique `(UserId, MovieId)`, standalone `(CreatedAtUtc)`). No column
change. §11.

---

## 6. Milestone 3.2 — Likes + Comments (contract)

**Scope:** like/unlike a review (idempotent, unique per user+review); **flat** comments on a review; live-ish
counts. (Notifications for like/comment are wired in 3.5; 3.2 may co-persist the `Notification` row now behind
the same handler and no-op the push until the notifier exists — see §9.)

### 6.1 Verticals

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `LikeReviewCommand(Guid ReviewId)` | Command | `IAppDbContext`, `ICurrentUser` | `LikeToggleResult(int LikeCount, bool LikedByMe=true)` | Insert `ReviewLike.Create(ReviewId, currentUser)`; **idempotent** — a duplicate trips the composite PK, caught as no-op. On a genuinely new like (and not a self-like) co-persist a `Notification` (§9). |
| `UnlikeReviewCommand(Guid ReviewId)` | Command | `IAppDbContext`, `ICurrentUser` | `LikeToggleResult(int LikeCount, bool LikedByMe=false)` | Remove the `(ReviewId, currentUser)` row if present; idempotent. |
| `AddCommentCommand(Guid ReviewId, string Body)` | Command (+validator) | `IAppDbContext`, `ICurrentUser` | `AddCommentResult(Guid CommentId, CommentVm Card)` | `Comment.Create(ReviewId, currentUser, Body)`; co-persist a `Notification` to the review author (§9). |
| `DeleteCommentCommand(Guid CommentId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load tracked; **ownership check → 403**; remove. |
| `GetReviewCommentsQuery(Guid ReviewId, CommentCursor? Cursor, int Take = 20)` | Query | `IAppDbContext` | `CommentListVm` (page + cursor) | **Public** read. Keyset on `(ReviewId, CreatedAtUtc)` — the existing index. Chronological (oldest-first or newest-first — a UX call; default oldest-first for a comment thread). |

- **Threaded vs flat — DECISION: FLAT.** The `Comment` entity has no `ParentCommentId`, and the existing
  index is `(ReviewId, CreatedAtUtc)`. Threaded replies would need a self-referencing column + migration +
  recursive rendering + N-level authZ — real scope with no spec mandate. **Phase 3 ships flat, chronological
  comments.** Threading is deferred; if wanted it is a scoped follow-up (add `ParentCommentId Guid?`, an
  index, and bounded-depth rendering). Flagged as a product/UX call.
- **Comment moderation — DECISION: author-only delete in Phase 3.** Only the comment's author can delete it.
  Letting the **review author** moderate comments on their review is reasonable but is a second ownership rule
  and a product call — **deferred**, flagged. (When added, `DeleteCommentCommand` allows delete if
  `comment.UserId == me` **or** the parent review's `UserId == me`.)
- **Self-like:** allowed to insert (product-neutral) but **never notifies** (skip the notification when
  `review.UserId == currentUser`). Because `LikeReviewCommand` is idempotent, the notification is created
  only on the *first* real like — a like→unlike→like does not spam.
- **Counts:** `LikeToggleResult.LikeCount` is recomputed from `ReviewLikes.Count(...)` after the write (one
  query); the `_ReviewCard` re-renders the count via an HTMX swap of the like button/count region.

### 6.2 Controllers & routes

- **`ReviewsController` (likes):** `POST /reviews/{id:guid}/like` → `LikeReviewCommand`; `DELETE
  /reviews/{id:guid}/like` → `UnlikeReviewCommand`; each returns the `_LikeButton` partial (count + pressed
  state) to swap.
- **`CommentsController` `[Authorize]`:** `POST /reviews/{id:guid}/comments` → `AddCommentCommand` (returns
  `_CommentCard` to append); `DELETE /comments/{id:guid}` → `DeleteCommentCommand` (empty 200 → remove node).
- **Public comments list** on `DiscoveryController` (or `CommentsController` `[AllowAnonymous]` GET):
  `GET /reviews/{id:guid}/comments?cursor=` → `_CommentList` partial.

### 6.3 Test plan (3.2)

| # | Test | Asserts |
|---|---|---|
| L1 | `Like_is_idempotent` | Two `LikeReviewCommand`s for the same `(user, review)` → one `ReviewLike` row; `LikeCount == 1`. |
| L2 | `Unlike_removes_and_is_idempotent` | Unlike removes the row; a second unlike is a no-op 200. |
| L3 | `Self_like_creates_no_notification` | Liking your own review inserts the like but **no** `Notification`. |
| L4 | `Add_comment_persists_and_encodes` | Comment persists with `UserId = current`; `<script>` body renders encoded. |
| L5 | `Delete_others_comment_is_403` | Deleting someone else's comment → 403; row unchanged. |
| L6 | `Comments_list_is_public_keyset` | Anonymous list paginates by `(ReviewId, CreatedAtUtc)` cursor with no overlap. |

**Migration:** none — `ReviewLike` composite PK `(ReviewId, UserId)` and `Comment` index `(ReviewId,
CreatedAtUtc)` already exist and serve every 3.2 query. §11.

---

## 7. Milestone 3.3 — Friends + profiles (contract)

**Scope:** friend request / accept / decline / remove (`Friend`, self-referencing directed); a public/limited
profile page; privacy-respecting queries.

### 7.1 Verticals

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `SendFriendRequestCommand(Guid AddresseeUserId)` | Command | `IAppDbContext`, `ICurrentUser` | `SendFriendRequestResult(FriendRequestOutcome)` | `Friend.Request(currentUser, AddresseeUserId)` (domain rejects self). **Pair guard:** check for an existing relationship in **either** direction first (§7.2). Co-persist a `FriendRequest` `Notification` to the addressee (§9). |
| `RespondToFriendRequestCommand(Guid RequestId, bool Accept)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Load `Friend`; **ownership check: only the `AddresseeId` may respond → 403**; `Accept()`/`Decline()` (domain rejects non-pending). On accept, co-persist a `FriendAccepted` `Notification` to the requester. |
| `RemoveFriendCommand(Guid OtherUserId)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit`/void | Remove the accepted `Friend` row between the two users (either direction). |
| `GetProfileQuery(Guid UserId)` | Query | `IAppDbContext`, `ICurrentUser` | `ProfileVm` (privacy-shaped) | §7.3 privacy rules. |
| `GetFriendsQuery()` | Query | `IAppDbContext`, `ICurrentUser` | `FriendsVm` | The current user's accepted friends (both directions), projected. |
| `GetPendingRequestsQuery()` | Query | `IAppDbContext`, `ICurrentUser` | `PendingRequestsVm` | Incoming (addressee = me) + outgoing (requester = me), `Status = Pending`. |

### 7.2 The directed-pair gap (REVIEW_BACKLOG) — enforced in the handler

The `Friend` unique index is directed `(RequesterId, AddresseeId)`, so `(A,B)` and `(B,A)` can coexist —
REVIEW_BACKLOG flags "one relationship per pair, enforce later." Phase 3 owns friend requests, so it must
handle it now:

- **`SendFriendRequestCommand` checks both directions before creating.** If any `Friend` row exists for the
  unordered pair `{me, addressee}`:
  - already `Accepted` → outcome `AlreadyFriends` (no-op);
  - a **pending outgoing** (me→them) → outcome `AlreadyRequested` (no-op);
  - a **pending incoming** (them→me) → outcome `ReciprocalPending` — **do not create a second row**; surface a
    "they already asked you — accept instead" affordance (product-friendly). (Auto-accepting is an
    alternative; default is to prompt, not silently accept.)
  - a prior `Declined` → product call: allow a fresh request (default) or block. Default: allow re-request by
    reusing/replacing the declined row (or inserting a new one) — flagged.
- The DB cannot cleanly enforce a canonical unordered pair while `RequesterId`/`AddresseeId` remain
  semantically directed, so the **handler is the guard**; the directed unique index still prevents exact
  duplicate directed rows. This is recorded as the resolution of the REVIEW_BACKLOG item for Phase 3.

### 7.3 Profile privacy (privacy-respecting queries)

`GetProfileQuery` shapes what the viewer sees by relationship. **Profiles require authentication in Phase 3**
(fail-closed; anonymous access to `IsProfilePublic` profiles is deferred, flagged §15):

| Viewer relationship to profile owner | Sees |
|---|---|
| **Owner** (`UserId == me`) | Everything (name, avatar, all their reviews, friend list, edit controls). |
| **Accepted friend** | Full profile (name, avatar, reviews, mutual-friend hints). |
| **Signed-in non-friend, owner `IsProfilePublic = true`** | Public profile (name, avatar, reviews) + "Add friend". |
| **Signed-in non-friend, owner `IsProfilePublic = false`** | **Limited** (name, avatar only) + "Add friend"; no reviews/activity. |
| **Anonymous** | Redirected to login (fail-closed). |

The query **projects only the fields the relationship permits** (it does not fetch reviews then hide them in
the view) — privacy is enforced at the data read, not the template. `IsProfilePublic` and friendship are
resolved in the same query.

### 7.4 Controllers & routes

- **`FriendsController` `[Authorize]`:** `GET /friends` (list + incoming/outgoing requests); `POST
  /friends/requests` `{ addresseeUserId }`; `POST /friends/requests/{id:guid}/accept`; `POST
  /friends/requests/{id:guid}/decline`; `DELETE /friends/{otherUserId:guid}`. Each returns the relevant
  partial (updated request row / friends list node).
- **`ProfilesController` `[Authorize]`:** `GET /users/{id:guid}` → `Profile` view (privacy-shaped). Profiles
  are addressed by `Guid` (there is **no username/handle** column — adding one is a schema + product decision,
  §15; Guid routes are functional now).

### 7.5 Test plan (3.3)

| # | Test | Asserts |
|---|---|---|
| F1 | `Send_request_creates_pending_and_notifies_addressee` | Pending `Friend` row + a `FriendRequest` `Notification` to the addressee. |
| F2 | `Cannot_friend_yourself` | Self-request → `DomainException` → 400 (domain guard). |
| F3 | `Reciprocal_request_does_not_create_second_row` | If B→A pending exists, A's send returns `ReciprocalPending` and creates no second row. |
| F4 | `Only_addressee_can_respond` | Requester (or a third party) responding → 403; addressee accept → `Accepted` + `FriendAccepted` notification to requester. |
| F5 | `Private_profile_hides_reviews_from_non_friend` | Signed-in non-friend sees limited profile (no reviews) when `IsProfilePublic = false`; sees reviews when true. |
| F6 | `Anonymous_profile_redirects_to_login` | Anonymous `GET /users/{id}` → 302 login (fail-closed). |

**Migration:** `IX_Friends_AddresseeId_Status` on `(AddresseeId, Status)` (incoming-request + reverse friend
lookup); optionally `IX_Friends_RequesterId_Status`. Existing: unique `(RequesterId, AddresseeId)` + Restrict
FKs. §11.

---

## 8. Milestone 3.4 — Activity feed (contract, ADR 0012)

**Scope:** the authenticated `/home` becomes the friends feed — friends' recent reviews, keyset-paginated,
infinite scroll. (Realizes the Phase-2 §7 "keyset for local feeds" reserve.)

- **`GetActivityFeedQuery(FeedCursor? Cursor, int Take = 20) : IRequest<ActivityFeedVm>`**; deps
  `IAppDbContext` + `ICurrentUser`. **Read-time query, no materialized feed** (ADR 0012):
  1. resolve accepted-friend ids (both directions, `Status = Accepted`);
  2. read `Reviews` where `UserId IN (friendIds)`, keyset `(CreatedAtUtc DESC, Id DESC)`, `Take+1` for
     `HasMore`, projected to `FeedItemVm` (author name/avatar, title + poster path, rating, body excerpt,
     like/comment counts, `LikedByMe`);
  3. cursor is an **opaque `(lastUtc, lastId)`** encoding — never a page number, never `OFFSET`.
- **Feed v1 = friends' REVIEWS only.** Friends' *likes/comments* as feed items are an **opt-in `UNION`
  extension** (ADR 0012 §3) — a heavier query, flagged as a product/UX call; default is reviews-only for a
  clean, high-signal feed. The denormalized `ActivityEvent` table is **deferred** behind the ADR-0012 §4
  trigger.
- **The feed excludes the current user's own reviews** (it is "your friends"); self-activity shows on the
  profile (§7).
- **Empty states:** no friends yet → a "find friends" empty state (not an error); friends but no reviews →
  a quiet "no recent activity."

**Controllers & routes:** `HomeController.Index` (`/home`, `[Authorize]`) renders the **feed shell**; a
`GET /home/feed?cursor=` action returns the `_FeedPage` partial (load-more sentinel, reusing the 2.4 keyset/
sentinel grammar). The layout nav's "Home" points at `/home` for signed-in users; `/discover` remains the
public browse surface (Phase-2 §10.1 separation holds).

**Test plan (3.4):**

| # | Test | Asserts |
|---|---|---|
| A1 | `Feed_shows_only_accepted_friends_reviews` | A sees B's review (A↔B accepted); does not see C's (not friends); does not see A's own. |
| A2 | `Feed_is_keyset_paginated_no_overlap` | Page 2 via the cursor returns strictly older items; no duplicates, no gaps as a new review is inserted mid-scroll. |
| A3 | `Feed_requires_auth` | Anonymous `/home` → 302 login. |
| A4 | `No_friends_shows_empty_state` | A with no friends → 200 with the find-friends empty state, not an error. |

**Migration:** `IX_Reviews_UserId_CreatedAtUtc` on `(UserId, CreatedAtUtc)` (feed per-friend keyset) and
`IX_Friends_AddresseeId_Status` (from 3.3, reused for friend-set resolution). §11.

---

## 9. Milestone 3.5 — Notifications + realtime (contract, ADR 0011)

**Scope:** create a `Notification` on relevant events; deliver live via **self-hosted SignalR**; a
notifications inbox + a live unread badge; `Device`/Web-Push **groundwork only** (Phase 6).

### 9.1 Notification creation — co-persist in the triggering handler

The event-producing handlers (`LikeReviewCommand`, `AddCommentCommand`, `SendFriendRequestCommand`,
`RespondToFriendRequestCommand`-accept) **persist the `Notification` row via `IAppDbContext` in their own
`SaveChangesAsync`** (atomic with the action — a like always yields its notification), then push (§9.2). No
separate command, no `ISender` inside a handler. Recipient/actor/target:

| Event | `Notification.Type` | Recipient | Actor | Target |
|---|---|---|---|---|
| A likes B's review | `ReviewLiked` | review author (B) | A | `ReviewId` |
| A comments on B's review | `CommentAdded` | review author (B) | A | `ReviewId` |
| A sends B a friend request | `FriendRequest` | B | A | the `Friend` request id |
| B accepts A's request | `FriendAccepted` | requester (A) | B | the `Friend` id |

Self-events do not notify (skip when actor == recipient). `Notification.Message` is a short server-composed
string (≤ `MessageMaxLength = 500`); the client deep-links via `Type` + `TargetId`.

### 9.2 Realtime push — behind `IRealtimeNotifier`, best-effort

- After the co-persisting `SaveChangesAsync` succeeds, the handler calls
  `IRealtimeNotifier.NotifyAsync(recipientId, dto, ct)` **best-effort** (try/catch → `LogWarning`; a push
  failure NEVER rolls back or fails the write). It also calls `UnreadCountChangedAsync(recipientId, count)`
  so the badge updates live.
- `IRealtimeNotifier` is an **Application port**; `SignalRRealtimeNotifier` (the adapter over
  `IHubContext<NotificationHub, INotificationClient>`) lives in **Web** and is registered in `Program.cs`
  (ADR 0011 §3 — Infrastructure stays out of realtime; the dependency rule holds).
- **`NotificationHub : Hub<INotificationClient>`**, `[Authorize]` (fail-closed — an anonymous socket must
  never join a group), group-per-user `user-{Context.UserIdentifier}` (the same `NameIdentifier` Guid
  `ICurrentUser` resolves). Mapped at `/hubs/notifications`.
- **Client:** `@microsoft/signalr` (MIT, free) bundled locally via esbuild (no CDN); `HubConnectionBuilder`
  with `.withAutomaticReconnect([0, 2000, 10000, 30000])` and an `onclose` "refresh to reconnect" affordance;
  on `ReceiveNotification` show an on-brand toast + prepend to the bell dropdown; on `UnreadCountChanged`
  update the badge.

### 9.3 Inbox verticals

| Request | Kind | Handler deps | Returns | Notes |
|---|---|---|---|---|
| `GetNotificationsQuery(NotificationCursor? Cursor, int Take = 20)` | Query | `IAppDbContext`, `ICurrentUser` | `NotificationListVm` | Recipient = current user only; keyset `(RecipientUserId, CreatedAtUtc DESC, Id DESC)`. |
| `GetUnreadCountQuery()` | Query | `IAppDbContext`, `ICurrentUser` | `int` | No-JS/badge fallback; uses `(RecipientUserId, IsRead)`. |
| `MarkNotificationReadCommand(Guid Id)` | Command | `IAppDbContext`, `ICurrentUser` | `Unit` | Load; only the recipient may mark (non-owned → `NotFound`); `MarkRead()`. |
| `MarkAllNotificationsReadCommand()` | Command | `IAppDbContext`, `ICurrentUser` | `int marked` | Bulk mark the current user's unread. |

**Controllers & routes:** `NotificationsController` `[Authorize]`: `GET /notifications` (inbox shell + first
page), `GET /notifications/feed?cursor=` (`_NotificationPage` partial), `POST /notifications/{id:guid}/read`,
`POST /notifications/read-all`, `GET /notifications/unread-count` (badge fallback). Hub at
`/hubs/notifications`.

### 9.4 Scope: built vs stubbed (ADR 0011 §4)

- **Built:** `NotificationHub` + auth + group-per-user; `IRealtimeNotifier` + Web adapter; live toast + live
  unread badge; inbox + mark-read; the co-persist wiring in the four triggering handlers.
- **Deferred/stub:** `FeedHub` live append (feed refreshes on navigation — a later polish); **Redis/Azure
  backplane** (single instance); **Web Push / `Device`** delivery for tab-closed users (Phase 6 — `Device`
  entity comment already says "Phase 6"; Phase 3 does no VAPID). A `RegisterDeviceCommand` stub may be
  scaffolded but is not wired to push.

### 9.5 Test plan (3.5)

| # | Test | Asserts |
|---|---|---|
| N1 | `Like_persists_notification_to_review_author` | A likes B's review → a `ReviewLiked` `Notification` with recipient B, actor A, target = ReviewId. |
| N2 | `Push_failure_does_not_fail_the_write` | `IRealtimeNotifier` throwing → the like/notification still commit; a Warning is logged. |
| N3 | `Hub_requires_auth` | An anonymous SignalR connection to `/hubs/notifications` is rejected (401/negotiate fails). |
| N4 | `Inbox_returns_only_my_notifications_keyset` | User sees only their own, newest-first, keyset-paginated. |
| N5 | `Mark_others_notification_read_is_not_found` | Marking a notification you don't own → `NotFound` (no cross-user mutation). |
| N6 | `Notifications_page_keeps_strict_CSP` | `GET /notifications` CSP unchanged: `script-src 'self'`, `connect-src 'self'` (covers the same-origin hub), no `unsafe-eval`. |

**Migration:** `IX_Notifications_RecipientUserId_CreatedAtUtc` on `(RecipientUserId, CreatedAtUtc)` for the
inbox keyset (existing: `(RecipientUserId, IsRead)` for the count, `(CreatedAtUtc)`). §11.

---

## 10. Anti-forgery / authZ / CSP contract (all Phase-3 endpoints)

A single restated contract every new endpoint honors (REVIEW_BACKLOG global contracts):

1. **Fail-closed authZ.** Write controllers (`Reviews`, `Comments`, `Friends`, `Profiles`, `Notifications`)
   are `[Authorize]`. The **only** `[AllowAnonymous]` Phase-3 reads are the **public title-reviews list and
   comments list** on the Details page. Profiles/feed/friends/notifications require auth. The hub is
   `[Authorize]`.
2. **Anti-forgery.** Every state-changing POST/PUT/DELETE carries the token via the already-wired
   `RequestVerificationToken` header (HTMX) — the global `AutoValidateAntiforgeryToken` validates it. No new
   anti-forgery wiring; no endpoint opts out (no webhooks in Phase 3). The layout `<meta>` token + `site.ts`
   `htmx:configRequest` handler (Milestone 1.6a) already cover HTMX `hx-post`/`hx-delete`.
3. **CSP — NO widening in Phase 3.** `script-src 'self'` (Alpine CSP build already in from 2.4), `style-src
   'self'`, `img-src 'self' https://image.tmdb.org data:` (posters/avatars — user avatars via `IFileStorage`
   are same-origin, covered by `'self'`), `connect-src 'self'` (covers **both** same-origin HTMX writes **and
   the same-origin SignalR WebSocket** `wss://<host>/hubs/notifications`). The SignalR JS client and all write
   glue are **locally bundled** (no CDN), so `script-src 'self'` is untouched. **Confirm** `connect-src
   'self'` admits the same-origin WS in the target browsers during the 3.5 live smoke; if a strict UA needs it
   spelled out, that is the *only* possible CSP note — do not widen preemptively.
4. **Rate limiting (recommended).** Add a per-IP/per-user `"social-write"` rate-limit policy (mirroring the
   Phase-1 `"auth"` policy) on the like/comment/review/friend POSTs to blunt spam (`security-hardening`:
   "rate-limit review/comment posts, not just login"). Config-overridable, relaxed in Dev/Testing like the
   auth policy. Flagged for security-agent; not a blocker for the mock-first builds.

---

## 11. Migrations / schema deltas (for database-agent — do NOT write here)

**No new entities and no column changes** — every Phase-3 feature uses the existing Phase-1 entities. Only
**indexes** are added (index-only migrations are low-risk). Specify per milestone:

| Milestone | Add index | On | Why | Existing (unchanged) |
|---|---|---|---|---|
| 3.1 | `IX_Reviews_MovieId_CreatedAtUtc` | `Review(MovieId, CreatedAtUtc)` | title reviews list keyset (newest-first) | unique `(UserId, MovieId)`; `(CreatedAtUtc)`; Restrict FKs; `CK_Review_Rating` |
| 3.3 | `IX_Friends_AddresseeId_Status` (opt. `..._RequesterId_Status`) | `Friend(AddresseeId, Status)` | incoming requests + reverse friend-set resolution | unique `(RequesterId, AddresseeId)`; Restrict FKs |
| 3.4 | `IX_Reviews_UserId_CreatedAtUtc` | `Review(UserId, CreatedAtUtc)` | friends-feed per-friend keyset | as 3.1 |
| 3.5 | `IX_Notifications_RecipientUserId_CreatedAtUtc` | `Notification(RecipientUserId, CreatedAtUtc)` | inbox keyset | `(RecipientUserId, IsRead)`; `(CreatedAtUtc)`; Restrict FKs |
| 3.2 | **none** | — | `ReviewLike` PK `(ReviewId, UserId)` + `Comment(ReviewId, CreatedAtUtc)` already serve every query | — |

- Migrations are generated in `Cinora.Infrastructure`, startup project `Cinora.Web`; apply via the reviewed
  **idempotent SQL script** / `db-update.ps1` (the app never auto-migrates). Prefer **small per-milestone
  migrations** (`ReviewListIndex`, `FriendLookupIndexes`, `FeedIndex`, `NotificationInboxIndex`) so each
  milestone is independently appliable, or one consolidated `Phase3SocialIndexes` at the phase start if the
  team prefers.

**Schema-insufficiency flags (honest):**
1. **Flat comments only** — no `Comment.ParentCommentId`. Threading is a deferred column + migration (§6).
2. **Directed `Friend` index allows reciprocal `(A,B)`+`(B,A)` rows** — enforced in the handler, not the DB
   (§7.2); the REVIEW_BACKLOG item is thereby resolved for Phase 3 at the application layer.
3. **No username/handle on `User`** — profiles are addressed by `Guid`. A clean-URL handle is a schema
   (unique column) + product decision (§15).
4. **Mixed-type activity feed** (reviews+likes+comments in one timeline) needs a `UNION` or a deferred
   `ActivityEvent` table (ADR 0012); Phase-3 feed v1 is reviews-only.
5. **Avatar storage** — `User.AvatarFileKey` exists (Phase-1); actually *uploading* avatars is **Phase 4**
   (`IFileStorage`), so Phase-3 profiles render the existing key or a default avatar; no upload endpoint here.

---

## 12. UI screens

(frontend-agent + ux-agent; skills: `premium-ui-design`, `razor-views`, `alpine-htmx-interactivity`,
`ui-animations`, `responsive-accessibility`, `signalr-realtime`.)

- **Details page (extends 2.5):** below the hero — the **write-your-review** form (auth users; rating input
  1–10 + textarea; anonymous users see a "Sign in to review" CTA), the current user's own review pinned with
  edit/delete (own-only controls), then the **public reviews list** (paginated cards with author, rating,
  encoded body, like button + count, comment count → expandable flat comment thread with an add-comment box).
  All writes are HTMX partial swaps (`hx-post`/`hx-delete` + token header); all user text Razor-encoded.
- **Profile page (`/users/{id}`):** avatar + display name, privacy-shaped body (reviews or limited), an
  **Add friend / Pending / Friends** button reflecting relationship state, friends count. Owner sees an edit
  affordance (display name + `IsProfilePublic` toggle — the profile-edit command is small; avatar upload is
  Phase 4).
- **Friends page (`/friends`):** incoming requests (Accept/Decline), outgoing (Pending), friends list
  (Remove). Actions are HTMX row swaps.
- **Feed (`/home`):** the friends feed shell + keyset infinite scroll (reuse the 2.4 self-replacing sentinel);
  premium review cards with poster thumbnails; empty states (find-friends / no-activity).
- **Notifications:** a nav **bell** with a live unread badge + dropdown (recent, mark-read), and a full
  `/notifications` inbox; a toast on live `ReceiveNotification`. Reduced-motion respected (`ui-animations`).
- **Accessibility:** HTMX-swapped regions carry `aria-live` where content updates (comment appends, feed
  pages, toast `role="status"`/`role="alert"`); rating input keyboard-accessible; the bell/dropdown keyboard
  + screen-reader navigable (`responsive-accessibility`). Alpine components use the **CSP build's name-only
  rule** (Phase-2 §11.2(d)) — logic lives in registered `Alpine.data` components, not inline expressions.

**CSP/contract extensions:** **none** (§10). This is a deliberate, load-bearing property — Phase 3 adds
realtime and writes without touching `SecurityHeadersMiddleware`.

---

## 13. Performance stance

(performance-agent; skills: `dotnet-performance`, `ef-core-data-access`, `signalr-realtime`.)

- **Keyset pagination everywhere** (reviews list, comments, feed, notifications) — opaque `(timestamp, id)`
  cursors, `Take+1` for `HasMore`, **never `OFFSET`** (deep-pagination cost). Backed by the §11 composite
  indexes.
- **`AsNoTracking().Select(...)` projections** for every read; **counts computed in one query** (correlated
  subqueries / group joins), never per-row round-trips (no N+1 in the reviews/feed cards). `LikedByMe` via a
  single bounded membership check.
- **Writes:** load a tracked entity → domain method → `SaveChanges`; unique-index races caught and reconciled
  (reuse the `EnsureTitleCached` pattern). Notification co-persist is one extra row in the same save.
- **Realtime is off the critical path:** push happens *after* `SaveChanges`, best-effort, and hub methods stay
  thin (no DB work inside the hub — the skill's rule); heavy work would enqueue to Hangfire (not needed in
  Phase 3).
- **Output caching:** the **public** title-reviews/comments partials MAY be `OutputCache`'d with a short TTL
  and `VaryByQuery(cursor)`, but **only the anonymous, non-personalized** variant (the authed variant varies
  by `LikedByMe`/`IsMine` per user — do not cache per-user state). The authenticated feed/inbox are **not**
  output-cached (per-user). This also lets 3.1 pick up the deferred REVIEW_BACKLOG 2.3 `OutputCache` policy on
  the shared Discovery surface.
- **Async end-to-end**, `CancellationToken` threaded controller → `ISender` → handler → EF/`IRealtimeNotifier`
  (no `.Result`, no sync-over-async).
- **Feed friend-set** is a bounded `IN (@friendIds)` parameter set; the ADR-0012 §4 trigger governs when a
  denormalized feed becomes warranted.

---

## 14. In-app chat — DEFERRED (ADR 0010)

Chat is in the **PRD** but **absent from the Backend Schema, the Implementation Plan, and Prompt.txt**
(`CLAUDE.md` "Known spec inconsistency"; `PROGRESS.md` Open Decision). **Phase 3 is designed and built WITHOUT
chat.** A future chat milestone would need `Conversation`/`Message` entities + migration, a `ChatHub`
(self-hosted SignalR, participants-only groups, fail-closed auth), the CQRS verticals
(`StartConversation`/`SendMessage`/history keyset/read), plain-text-Razor-encoded messages, a send rate-limit,
and (for offline) Web Push — plus the product scope to settle (1:1 vs group, attachments, retention). The
Phase-3 realtime foundation (ADR 0011) is deliberately **chat-ready** so a later `ChatHub` is additive.

> **FLAG TO USER (product decision):** confirm chat is **out** of Phase 3. If chat is ruled **in**, it becomes
> a separately-scoped milestone (recommended after Phase 4), not a silent addition here. This design proceeds
> **without** chat per ADR 0010.

---

## 15. Key risks and product decisions

**Risks baked into the design:**
- **`ICurrentUser` correctness under Identity.** The adapter reads `NameIdentifier`; the same claim backs
  SignalR `Context.UserIdentifier`. If they ever diverge, server pushes address the wrong group — covered by
  N1/N3/N4 tests and a live smoke.
- **Notification/like co-persist coupling.** The like/comment/friend handlers now also write a `Notification`
  row. Accepted (it keeps the notification atomic with the action); if the coupling grows, a domain-event
  dispatch after `SaveChanges` is the refactor (no such infra today — deferred).
- **Realtime best-effort delivery.** A dropped socket means a missed live toast, never a missed notification
  (persistence is the source of truth; the inbox is always correct). Single-instance, no backplane (ADR 0011)
  — a second instance would miss cross-node pushes (documented scale trigger).
- **Directed-pair friend race.** Two simultaneous `SendFriendRequestCommand`s for the same pair could both
  pass the handler check before either commits; the directed unique index still blocks exact duplicates, and
  a rare reciprocal double is reconcilable — mitigated by the handler guard + index, acceptable for the local
  profile.
- **Feed friend-set size** (`IN` clause) — bounded in practice; the ADR-0012 §4 trigger governs escalation.

**Genuine PRODUCT / UX decisions (defer to the user or ux-agent — NOT architecture):**
- **In-app chat in/out** (ADR 0010) — recommend **out** of Phase 3.
- **Threaded vs flat comments** — Phase 3 ships **flat**; threading is a deferred column + migration.
- **Comment moderation by the review author** — Phase 3 is **author-only delete**; review-author moderation
  deferred.
- **Feed content: reviews-only vs reviews+likes+comments** — Phase 3 ships **reviews-only**; the `UNION`
  mixed-feed is an opt-in extension (ADR 0012 §3).
- **Anonymous access to public profiles** — Phase 3 requires **auth** for all profiles; anonymous view of
  `IsProfilePublic` profiles is deferred.
- **Username/handle for clean profile URLs** — Phase 3 uses **Guid** routes; a handle is a schema + product
  decision.
- **Self-like / self-follow semantics** — self-like allowed but never notifies; revisit if undesired.
- **`MinCommentLength`, rating input style (stars vs number), feed page size** — UX defaults, overridable.

---

## 16. Phase 3 milestone build order (delegable)

Ordered milestones for the orchestrator. Each states the owning agent(s), the deliverable, the exact `Verify`
command/acceptance, and the review gates to run (`/review-architecture`, `/review-code`, `/review-security`,
`/review-performance`, `/review-ui`) per `CLAUDE.md` (automatic after every milestone and at phase end). All
commands run from the repo root unless noted. Phase 3 is **mock-first for TMDB** (the existing faked
`ITmdbClient`) and runs against real **LocalDB `CinoraTest`** in integration tests. **No new external key is
needed** (TMDB v4 key already set; Google OAuth optional).

> **Prerequisite (fold into 3.1):** build the `ICurrentUser` port + `CurrentUser` adapter +
> `AddHttpContextAccessor()` and the `ForbiddenAccessException`→403 arm on `GlobalExceptionHandler` FIRST —
> every subsequent milestone depends on them (ADR 0009).

### 3.1 — Current-user seam + Reviews CRUD
- **Owner:** backend-agent (verticals/controllers) + database-agent (index) + frontend-agent + ux-agent
  (Details review UI) + security-agent (ownership/authZ) + architecture-agent sign-off on the `ICurrentUser`
  seam + the `EnsureTitleCached`→`CreateReview` orchestration.
- **Deliverable:** `ICurrentUser` + `CurrentUser` + `AddHttpContextAccessor`; `ForbiddenAccessException` +
  403 mapping; `Create/Edit/DeleteReviewCommand` (+validators), `GetTitleReviewsQuery`,
  `GetMyReviewForTitleQuery` (`Cinora.Application/Features/Reviews/`); `ReviewsController` (writes) + public
  reviews-list partial on `DiscoveryController`; Details-page review UI (write/edit/delete + list, all
  Razor-encoded); `IX_Reviews_MovieId_CreatedAtUtc`.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet build Cinora.sln -c Release
  dotnet test tests/Cinora.Application.Tests/Cinora.Application.Tests.csproj
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests R1–R7 (§5.4) green: own-only edit/delete (403 cross-user), one-review-per-title, cascade on delete,
  public keyset list, **HTML-encoded body**, rating bounds. Build clean (TWAE on). Then `/review-security`
  (ownership + anti-forgery + XSS), `/review-code`, `/review-ui`.

### 3.2 — Likes + Comments
- **Owner:** backend-agent + frontend-agent + ux-agent.
- **Deliverable:** `Like/UnlikeReviewCommand`, `AddCommentCommand`, `DeleteCommentCommand`,
  `GetReviewCommentsQuery`; like button + count swap; flat comment thread (add/delete/list); the
  co-persist-`Notification` hook stubbed (push no-ops until 3.5). No migration.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests L1–L6 (§6.3): idempotent like/unlike, self-like no-notification, encoded comments, cross-user
  comment delete = 403, public comment keyset. Then `/review-security`, `/review-code`, `/review-ui`.

### 3.3 — Friends + profiles
- **Owner:** backend-agent + database-agent (indexes) + frontend-agent + ux-agent + security-agent (privacy).
- **Deliverable:** `SendFriendRequestCommand` (+ directed-pair guard), `RespondToFriendRequestCommand`,
  `RemoveFriendCommand`, `GetProfileQuery` (privacy-shaped), `GetFriendsQuery`, `GetPendingRequestsQuery`;
  `FriendsController`, `ProfilesController`; friends + profile UIs; `IX_Friends_AddresseeId_Status`.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests F1–F6 (§7.5): request/accept/notify, no self-friend, no reciprocal double-row, addressee-only respond
  (403 otherwise), private-profile hides reviews, anonymous profile → login. Then `/review-security` (privacy
  + authZ), `/review-code`, `/review-ui`.

### 3.4 — Activity feed (`/home`)
- **Owner:** backend-agent + performance-agent (keyset/indexes) + frontend-agent + ux-agent.
- **Deliverable:** `GetActivityFeedQuery` (+ `FeedCursor`), read-time reviews-only feed (ADR 0012);
  `HomeController.Index` feed shell + `GET /home/feed` keyset partial (2.4 sentinel grammar);
  `IX_Reviews_UserId_CreatedAtUtc`; empty states.
- **Verify (mock-first, LocalDB):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  ```
  Tests A1–A4 (§8): friends-only content (excludes self + non-friends), keyset no-overlap, auth-required,
  empty state. Then `/review-performance` (keyset + no N+1 in feed cards), `/review-ui`, `/review-code`.

### 3.5 — Notifications + realtime (self-hosted SignalR)
- **Owner:** backend-agent + security-agent (hub auth) + frontend-agent (client/toast/badge) +
  architecture-agent sign-off on the `IRealtimeNotifier` placement (ADR 0011).
- **Deliverable:** co-persist `Notification` in the four triggering handlers (wire the 3.2/3.3 stubs);
  `IRealtimeNotifier` + `NotificationDto`; `NotificationHub` + `INotificationClient` (`[Authorize]`,
  group-per-user) + `MapHub("/hubs/notifications")`; `SignalRRealtimeNotifier` registered in `Program.cs`;
  `AddSignalR()`; `@microsoft/signalr` client bundled locally + toast + live unread badge; inbox verticals +
  `NotificationsController`; `IX_Notifications_RecipientUserId_CreatedAtUtc`. `Device`/Web-Push **groundwork
  only** (Phase 6).
- **Verify (mock-first, LocalDB + bundle):**
  ```
  dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj
  npm run build --prefix src/Cinora.Web
  ```
  Tests N1–N6 (§9.5): notification co-persist + recipient scoping, **push failure doesn't fail the write**,
  **hub requires auth**, inbox keyset, no cross-user mark-read, **CSP unchanged** (`connect-src 'self'`
  covers the hub, no `unsafe-eval`); the bundle builds with the SignalR client and no CDN. **Live smoke
  (manual):** two browser sessions — A likes B's review → B sees a live toast + badge increment without
  reload; confirm `connect-src 'self'` admits the same-origin WS. Then `/review-security` (hub auth + CSP),
  `/review-performance` (thin hub, push off critical path), `/review-ui`, `/review-code`.

**End-of-phase gate (Exit Criteria):** `dotnet build Cinora.sln -c Release` clean (TWAE on); `dotnet test`
green across all four test projects (mock-first, LocalDB); review CRUD with own-only edit/delete, likes +
flat comments, friends + privacy-shaped profiles, the keyset friends feed at `/home`, and live SignalR
notifications all functioning; the `EnsureTitleCached`→`CreateReview` orchestration exercised (ADR 0008 seam's
first real consumer); **CSP unchanged**; **zero Critical/High** findings across the five review gates. Run the
full review-gate loop at phase end (`/review-architecture` + the four others).

---

_Design authored 2026-07-03 against the Phase-1/2 code (verified: `Review`/`ReviewLike`/`Comment`/`Friend`/
`Notification`/`Device`/`User`/`Movie` entities + their EF configurations and indexes; `IAppDbContext`
(+`DiscardPendingChanges`); `EnsureTitleCachedCommand` seam (ADR 0008); `ISender` pipeline +
`GlobalExceptionHandler`; `Program.cs` fail-closed authZ + `AutoValidateAntiforgeryToken` + Identity cookie;
`SecurityHeadersMiddleware` strict CSP; DiscoveryController conventions; **no `ICurrentUser`/SignalR/chat types
exist in `src/`**). No application code written._
