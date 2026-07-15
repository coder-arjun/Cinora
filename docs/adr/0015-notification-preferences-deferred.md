# ADR 0015 — Notification Preferences Deferred to Phase 6

- **Status:** Accepted
- **Date:** 2026-07-03
- **Phase:** 4 (Watchlists & Profile) — decision to defer to Phase 6 (Polish & Deployment / push)
- **Deciders:** architecture-agent (pre-implementation design), orchestrator (ratify), **user to ratify the
  product call**

## Context

The Phase-4 brief lists, under Account settings, "**notification preferences** (feeds Phase 6 push opt-in)."
Two facts constrain this:

1. **There is no schema field for notification preferences.** The `Backend_Schema` entities are
   `User, Friend, Movie, Genre, MovieGenre, Review, ReviewLike, Comment, Watchlist, Notification, Device,
   AIRecommendationHistory`. `Notification` is a *delivered* in-app row; `Device` is a *Web-Push transport*
   registration (Phase 6). **None** stores a per-user *preference* (e.g. "push me on likes", "email me on
   friend requests", "mute AI recommendations").
2. **Phase 4 ships no delivery channel a preference would govern.** Web Push (VAPID + `Device`) is **Phase
   6**; there is no email channel. In-app notifications (Phase 3) are already best-effort and always
   persisted. A preference toggle in Phase 4 would gate nothing that exists.

This mirrors the Phase-3 situation with in-app chat (ADR 0010): a PRD/brief item the current schema does not
support, which must be either scoped-and-built deliberately or deferred with its future shape recorded —
never silently half-built.

## Decision

**Defer notification preferences to Phase 6, where Web Push actually lands and a delivery channel exists for
a preference to govern.** Phase 4 delivers the **privacy** setting (public / friends-only, via the existing
`User.IsProfilePublic`, §4.3 of the Phase-4 design) — a *visibility* control, not a *notification-delivery*
preference — and does **not** add a preferences field, entity, or toggle.

- The Settings page MAY show a **disabled placeholder** ("Notification preferences — arriving with push in
  Phase 6") for roadmap discoverability, or omit the row entirely (a UX call).
- **Future shape (Phase 6, non-binding):** when push lands, add a preferences store owned by the user —
  either a small set of columns on `User` (e.g. per-event opt-in flags) or a dedicated
  `NotificationPreference` value/entity keyed by `UserId` (per channel × per event). The producing handlers
  (like/comment/friend/AI) and the push dispatcher consult it before delivering; the in-app row may still
  always persist (source of truth), with the preference gating *push/email* fan-out. This is designed
  **with** the Phase-6 push work, not ahead of it.

## Consequences

**Positive**
- No YAGNI dead config: nothing in Phase 4 reads a preference, so none is built; the schema stays
  index-only-migration clean this phase (§10 of the Phase-4 design).
- The preference model is designed against a real delivery channel (Phase 6), so it fits push/email/in-app
  fan-out correctly instead of guessing now.
- Honest scope: the brief item is deferred behind an ADR with a recorded future shape, not silently dropped.

**Negative / accepted costs**
- The Phase-4 Settings page is privacy-only (plus an optional disabled placeholder); a user expecting
  notification toggles now sees a "coming with push" affordance.
- A schema addition (columns or a `NotificationPreference` entity) is a Phase-6 migration — additive, not a
  rework of Phase-4 code.

## Alternatives considered

1. **Add a minimal `User.NotificationOptIn` (or a `NotificationPreference` entity) now.** Rejected for Phase
   4: nothing consumes it (no push/email), so it is dead config whose shape is likely wrong until the Phase-6
   push design exists. Offered to the user as an option if they want a visible toggle immediately (then it is
   a scoped schema + settings addition, flagged).
2. **Build push in Phase 4 to justify the preferences.** Rejected: Web Push (VAPID/`Device`) is explicitly
   **out of Phase 4 scope** (the brief's "Out of Scope: push notifications, offline") and is Phase 6.
3. **Repurpose `IsProfilePublic` as a notification preference.** Rejected: it is a *visibility* setting with
   distinct semantics (§7.3 profile privacy tiers); conflating the two would break both.

## Related
- ADR 0010 (in-app chat deferred — the same "PRD item absent from schema → defer behind an ADR" pattern),
  ADR 0011 (self-hosted SignalR realtime; `Device`/Web-Push groundwork only, Phase 6).
- `docs/architecture/phase-4-watchlists-profile-design.md` §8 (deferral), §4.3 (the privacy setting that
  *is* delivered), §13 (product decisions — flagged to user).
- `Backend_Schema.docx` (no preferences entity); `Implementation_Plan.docx` (Phase 6 = push/offline/polish).

---

_Design-only ADR authored 2026-07-03 against the schema (verified: no notification-preference field on any
entity; `Device` is Web-Push transport; `User.IsProfilePublic` is a visibility toggle). No application code
written; this records a scope/product decision for the user to ratify._
