# Re-deploy checklist — Feature batch (movie likes, series, person pages, chat search, motion, login hero) (2026-07-18)

An update to the live MonsterASP.NET app (`site78342.siteasp.net`, DB `db58983`). Full mechanics in
[`monsterasp-filezilla-deploy.md`](monsterasp-filezilla-deploy.md). **The app never auto-migrates — Step 1 is run
by hand.** This env can't reach prod SQL :1433, so the DB step is yours; the file upload is the agent's.

## What changed (11 features)
1. **Friend-list privacy** — a friend visiting your profile no longer sees your friend list (owner-only; the count stays).
2. **Chat search** — a search box in the Chat list filters your conversations (friends' DMs + groups) by name (client-side).
3. **Movie love button** — a per-user ❤ with animation + public count on every poster (rails, search, Details); new `MovieLikes` table.
4. **Discover “Most Loved”** — a new rail of the community's most-loved movies (Top-Rated rails already cover “by rating”).
5. **/home heading** — “Home” → **“Suggestions”** (eyebrow “Your feed” kept).
6. **Motion overhaul** — scroll-reveal system (IntersectionObserver), staggered reveals, richer micro-interactions; reduced-motion respected.
7. **Series** — Discover now shows Trending/Popular/Top-Rated **Series** rails alongside Movies.
8. **Search order** — search results sorted newest-release-first (per page).
9. **Cast → actor page** — clicking a cast member opens `/discover/person/{id}` with their best-rated titles (new TMDB person endpoint).
10. **Dynamic icons** — cohesive animated iconography (nav/brand hover motion) + the login feature chips.
11. **Login hero** — a cinematic split sign-in screen reflecting Cinora's vision (form unchanged, only relocated).
12. **Unified search** — the search box now spans **movies AND series** in one result set (TMDB `search/multi`);
    mixed-media results resolve watchlist/love state per `(id, media)` so overlays stay correct. No DB change.

## Schema — ONE new additive migration
- **`AddMovieLike`** — new `MovieLikes` table (composite PK `(MovieId, UserId)`, `LikedAtUtc`, FKs to `Movies`/`Users`,
  index on `MovieId`). Empty on creation; the app fills it as users tap ❤.

It is in `artifacts/migrate.sql` (idempotent). **The `MovieLikes` table MUST exist before the new app serves a
request** — the discovery rails and search resolve each viewer's love state for their cards, so without the table
those lazy rails would degrade to their retry state. **Apply the migration BEFORE the file upload.**

## Step 1 (you) — apply the migration to `db58983`
SSMS / Azure Data Studio → Server `db58983.databaseasp.net`, Login `db58983`, Database `db58983` → open
`artifacts/migrate.sql` → Execute. Idempotent; it applies only the pending `AddMovieLike` migration.

## Step 2 (agent) — FTPS upload (needs your go-ahead + current FTP credentials)
`app_offline.htm` up → upload the 4 changed `Cinora.*.dll` (Domain, Application, Infrastructure, Web) + the current
`/dist` assets (+`.br`/`.gz`) + `manifest.json` + `sw.js` → SHA-256 verify → remove `app_offline.htm`.
`App_Data/` + `appsettings.Production.json` preserved.

## Step 3 — smoke test
1. `/health` → 200 Healthy.
2. Open Discover → **Most Loved** + **Series** rails render; a ❤ on any poster toggles + the count updates.
3. Open a movie → tap a **cast member** → their top titles page loads; **Send to a friend** still works.
4. `/chat` → the **search box** filters conversations; `/home` heading reads **Suggestions**.
5. `/Account/Login` (sign out first) → the **cinematic hero** shows; sign-in still works.

## After deploy
- **Rotate** the `db58983` + FTP passwords if they were shared in chat. Delete throwaway test accounts.
- Verified locally before handoff: `dotnet build` 0/0 (TreatWarningsAsErrors on), `npm run build` clean, **546 tests pass**.

_Prepared 2026-07-18. Agents do not run git; a prod deploy needs your explicit go-ahead + credentials._
