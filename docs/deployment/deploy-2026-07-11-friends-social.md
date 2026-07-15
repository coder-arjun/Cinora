# Re-deploy checklist — Friends & Social overhaul + PWA image fix + login-only (2026-07-11)

This is an **update** to the already-live MonsterASP.NET app (`site78342.siteasp.net`, DB `db58983`), not a
first install. Follow the full mechanics in [`monsterasp-filezilla-deploy.md`](monsterasp-filezilla-deploy.md);
this file adds only what is different for THIS change. **The app never auto-migrates; you run everything below.**

## What changed in this build
- **Posters load directly from `image.tmdb.org`** (the server-side `/tmdb-img` proxy is gone) → fixes the blank
  thumbnails on the host.
- **Login-only:** Discover/Search/Details/reviews/comments now require sign-in.
- **Blocking** (new `UserBlocks` table), **exact-match user search** (username/email), redesigned **`/friends`**
  hub (Find people + WhatsApp invite + Cancel outgoing + Blocked), repointed empty-state CTAs.
- **Schema:** ONE new additive migration — **`AddUserBlock`** (new empty `UserBlocks` table + 2 indexes + FKs).

## Pre-built artifacts (ready in this repo)
| Artifact | Path |
|---|---|
| Deployable app (framework-dependent) | `./publish/` (freshly published) |
| Idempotent schema script (incl. `AddUserBlock`) | `./artifacts/migrate.sql` |

## Step 1 — Apply the migration to `db58983` (BEFORE uploading the app)
1. Connect to the MonsterASP MSSQL DB remotely (SSMS / Azure Data Studio):
   **Server** `db58983.databaseasp.net`, **User** `db58983`, **Password** (from the panel), **Database** `db58983`.
2. **Select `db58983`** in the query window (triple-check it's not another project's DB).
3. Open `artifacts/migrate.sql` and **Execute**. It's idempotent — it applies only migrations not already in
   `__EFMigrationsHistory`. Expect it to create the **`UserBlocks`** table (a brand-new, empty table — no data
   caveat).
   - ⚠️ **If this DB predates Phase 6** (i.e. it's missing `Phase6PushAndPreferences`), the idempotent script will
     also apply that migration, whose new **unique index on `Devices(UserId, EndpointHash)`** collides if
     `Devices` already has rows with the old `''` default. **Before running, check:** `SELECT COUNT(*) FROM Devices;`
     — if it's > 0 and the DB is pre-Phase-6, clear stale rows first (`DELETE FROM Devices;` — subscriptions
     re-register on next visit) or apply per the runbook §5.3. If the DB is already at Phase 6, this doesn't apply.

## Step 2 — Upload the app with FileZilla
Upload the **contents of `./publish/`** into the site web root (`/httpdocs`), per the FileZilla guide §6. For a
**re-deploy, PRESERVE these on the server — do NOT delete or overwrite them:**
- **`appsettings.Production.json`** — holds your secrets (connection string, Tmdb/Groq keys, `FileStorage:UrlSigningKey`).
  It is **not** in `./publish/` (secrets never ship in the build), so a plain upload won't touch it — just don't delete it.
- **`App_Data/`** — Data-Protection keys (keeps users logged in across restarts) + avatar uploads. Keep it.

Notes:
- The freshly-published **`web.config`** has `ASPNETCORE_ENVIRONMENT=Production` and stdout logging **ON** (useful to
  verify this deploy). After you confirm it's healthy, set `stdoutLogEnabled="false"` and re-upload `web.config`.
- If `.NET 10` isn't on the host, use the self-contained fallback (FileZilla guide §10).

## Step 3 — Smoke test (in the deployed browser)
1. `https://<your-domain>/health` → **200 Healthy** (connection string + migration are good).
2. **Anonymous** visit to `/discover` → you're **redirected to `/account/login`** (login-only works).
3. Sign in → **movie posters now load** (Network tab: poster requests go to `https://image.tmdb.org/...`, 200 — no
   more `/tmdb-img` 302-to-placeholder).
4. `/friends` → the **Find people** search box + WhatsApp invite + **Blocked** section render. Search a friend's
   **exact username or email** → their card appears; block someone → they get a 404 on your profile and vanish from
   your search.

## After deploy
- **Rotate** the DB + FTP passwords that were shared in chat.
- The *For You* AI rail stays empty until the nightly Groq Hangfire job runs (or you trigger `/jobs` as admin) —
  unchanged by this deploy.

_Prepared 2026-07-11. Agents do not run git or deploy; you execute Steps 1–3._
