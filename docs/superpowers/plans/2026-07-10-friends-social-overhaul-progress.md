# Friends & Social Overhaul — Execution Progress Ledger

Plan: `docs/superpowers/plans/2026-07-10-friends-social-overhaul.md`
Execution mode: subagent-driven-development, adapted for a **no-git** project (no worktree/commits;
reviewers read changed files against the task brief; each task ends at a build+test Checkpoint; the human
commits). Started 2026-07-10.

## Status

- [x] B0.1 — TmdbImageTagHelper → direct TMDB URLs; retire proxy
- [x] B1.1 — UserBlock domain entity
- [x] B1.2 — Persistence: DbSet + config + AddUserBlock migration
- [x] B1.3 — Block/unblock/list verticals + BlockQueries guard
- [x] B1.4 — Enforce blocking in SendFriendRequest + GetProfile
- [x] B1.5 — Web: Block/Unblock endpoints + profile control + Blocked card
- [x] B1.6 — ADR 0022
- [x] B2.1 — IUserDirectory port + adapter + DI
- [x] B2.2 — SearchUsersQuery
- [x] B2.3 — Search endpoint + result partial
- [x] B3.1 — CancelFriendRequestCommand
- [x] B3.2 — Tabbed /friends hub
- [x] B3.3 — Repoint find-friends empty-state CTAs
- [x] B4.1 — Remove [AllowAnonymous] from content
- [x] B4.2 — ADR 0023

## Log

Task B0.1: complete — TmdbImageTagHelper uses ITmdbImageUrlBuilder (direct image.tmdb.org URLs); TmdbImageController deleted; NSubstitute wired into Web.IntegrationTests (CPM pinned 5.3.0); 4 collateral proxy-path test assertions corrected to direct URLs; 1 proxy test file removed. Build 0/0; Web.IntegrationTests 185/185. Review: Spec ✅, Approved.
Task B1.1: complete — UserBlock domain entity (mirrors Friend.cs; self-block throws DomainException) + UserBlockTests (2/2). Build 0/0. Self-reviewed against spec (verbatim), approved.
Task B1.2: complete — IAppDbContext.UserBlocks + CinoraDbContext.UserBlocks + UserBlockConfiguration (unique (Blocker,Blocked) + (Blocked) indexes, Restrict FKs) + AddUserBlock migration (additive: CreateTable+2 indexes; Down=DropTable) + artifacts/migrate.sql regenerated + 3 Application test-fakes stubbed. Build 0/0; full suite 482 green. localhost SQL reachable this session. Self-reviewed, approved. CARRY-FORWARD: seed real users via TestAuthentication.RegisterAndSignInAsync (FK to Users rejects raw GUIDs).
Task B1.3: complete — BlockQueries (AreBlockedEitherWay + BlockedOrBlockedByIds), BlockUserCommand (idempotent, silent, removes friendships both ways), UnblockUserCommand, GetBlockedUsersQuery (+VMs). 4/4 BlockingTests via direct-handler construction + NSubstitute ICurrentUser. Build 0/0. Self-reviewed, approved. Note: subagent hit Grep/Glob EUNKNOWN uv_spawn errors, fell back to PS/Bash (env glitch, not code).
Task B1.4: complete — SendFriendRequestCommand +FriendRequestOutcome.Blocked + block pre-check (neutral msg); ProfileVm +ProfileRelationship.BlockedByMe; GetProfileQuery block resolution (owner-blocks-viewer→404, viewer-blocks-owner→BlockedByMe minimal); FriendsController.MessageFor neutral Blocked arm. Handler-level BlockEnforcementHandlerTests 4/4. NOTE: implementer API-stalled before writing report, but I verified independently — build 0/0, B1.4 tests 4/4, full Friends namespace 16/16 (no regression).
Task B1.5: complete — FriendsController Block/Unblock endpoints; _ProfileFriendControl BlockedByMe(Unblock) arm + icon-only secondary Block affordance in all non-owner arms using the REAL Phase-6.5 styled confirm (data-confirm-* → onHtmxConfirm → _ConfirmDialog); _BlockedUserCard (mirrors _FriendCard, real AvatarViewModel 4-arg signature); HTTP BlockEnforcementTests 1/1. Build 0/0; full Web.IntegrationTests 195/195; npm clean. PREMIUM UI verified (tokens/encoding/a11y/confirm). Self-reviewed, approved.
Task B1.6: complete — ADR 0022 (user-blocking model) authored by controller (docs-only), matches house ADR format.
Task B2.1: complete — IUserDirectory port (Application, Identity-free) + UserDirectory adapter (Infrastructure, UserManager.FindByEmailAsync) + DI registration after ICurrentUser. UserDirectoryTests 1/1 (exact email case-insensitive → UserId; unknown → null). Build 0/0. Verbatim; dependency rule compiler-enforced. Approved.
Task B2.2: complete — SearchUsersQuery (+UserSearchVm/UserSearchResultVm/validator/handler); email→IUserDirectory, username→User.Normalize(trim+upper) exact seek; excludes self + BlockedOrBlockedByIds; empty result = no-leak. Handler-level UserSearchTests 5/5. Build 0/0. NOTE: 2nd implementer API-stalled before writing the file; I authored B2.2 directly (confirmed User.Normalize=Trim().ToUpperInvariant(), computed the normalized term before the EF query so it parameterizes).
Task B2.3: complete (subagent) — FriendsController.Search (GET /friends/search) + premium _UserSearchResult partial (reuses _FriendCard/_ProfileFriendControl grammar; confirmed Profile route + real _Avatar). UserSearchEndpointTests 2/2; 12/12 no regression; npm clean; build 0/0.
Task B3.1/B3.2/B3.3: complete (authored inline for speed) — CancelFriendRequestCommand + FriendsController.Cancel; redesigned /friends hub (Find people search box + _InviteFriends → Requests w/ Cancel on outgoing → Friends → Blocked section) via FriendsPageVm+Blocked & GetBlockedUsersQuery; repointed Home no-friends CTA (Discover→Find friends) + Friends empty-state CTA. CancelRequestTests + FriendsHubTests. Build 0/0; npm rebuilt (app-6126237582.css); full Friends namespace 28/28.
Task B4.2: complete — ADR 0023 (authenticated-only access) authored by controller.
Task B4.1: complete — removed [AllowAnonymous] from DiscoveryController (class) + CommentsController.List; kept allowlist (landing/auth/tour/pwa/media/health/diagnostics); AnonymousLockdownTests (content→302, allowlist→200). Lockdown broke 29 pre-existing anonymous-browse tests (expected) → subagent converted them to authenticated + renamed the 6 "anonymous" tests, removed unused CreateClient helpers, kept all assertions. Build 0/0.

ALL 15 TASKS COMPLETE. Final verification (2026-07-11): dotnet test Cinora.sln -c Release → Domain 58/58, Application 128/128, Infrastructure 110/110, Web.IntegrationTests 212/212 = 508 passed, 0 failed. Build 0/0 (TWAE on). npm run build clean. CSP byte-unchanged (test-verified: img-src 'self' https://image.tmdb.org data:; script-src 'self'; no unsafe-eval). One additive migration (AddUserBlock). NOT git-committed (human does), NOT deployed (human-gated).

STANDING REQUIREMENT (user, 2026-07-10): all NEW UI (B1.5 profile Block/Unblock + Blocked card, B2.3 search results, B3.2 /friends hub, B3.3 empty states) must be ULTRA-PREMIUM — match the existing dark-luxury glassmorphism design system exactly (app.css @theme tokens, surface/elevation, premium typography/spacing, smooth reduced-motion-aware animation, responsive, WCAG AA). Use premium-ui-design/frontend-design skills; run /review-ui on the hub.

## Minor findings roll-up (for the final whole-branch review)

- B0.1 (Minor): `tests/Cinora.Web.IntegrationTests/Discovery/DetailsTests.cs:62` asserts only the `https://image.tmdb.org/t/p/` prefix, looser than sibling suites which pin the full URL. Real assertion, just weaker — consider tightening.
