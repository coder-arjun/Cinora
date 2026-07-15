# Re-deploy checklist — Chat improvements (session persistence, movie sharing, emoji, push, timezone) (2026-07-15)

An update to the live MonsterASP.NET app (`site78342.siteasp.net`, DB `db58983`). Full mechanics in
[`monsterasp-filezilla-deploy.md`](monsterasp-filezilla-deploy.md). **The app never auto-migrates — Step 1 is run
by hand.** This env can't reach prod SQL :1433, so the DB step is yours; the file upload is the agent's.

## What changed
1. **Session persistence (fixes "logged out again and again"):** Data-Protection keys now persist to the **DB**
   (survives restarts/redeploys) + sign-ins are forced **persistent** (30-day cookie). ⚠️ **This deploy logs every
   currently-signed-in user out ONCE** (the old filesystem key ring is abandoned); afterwards they stay signed in
   durably.
2. **Movie sharing** in chat (Details "Send to a friend" → `/chat/share` picker → movie card in the thread).
3. **Redesigned emoji picker** (tabbed, ~400 emojis).
4. **Mobile Web Push on new messages** (friendly title + preview + `/chat/{id}` deep link).
5. **Timezone fix** (chat times shown in the viewer's local time).

## Schema — TWO new additive migrations
- **`AddDataProtectionKeys`** — new `DataProtectionKeys` table (empty; the app fills it on first run).
- **`AddChatMovieShare`** — 4 nullable columns on `Messages` (`SharedMovieTmdbId`/`SharedMovieMediaType`/
  `SharedMovieTitle`/`SharedMoviePosterPath`).

Both are in `artifacts/migrate.sql` (idempotent). **The `DataProtectionKeys` table MUST exist before the new app
serves a request** (the app persists keys there on first use) — so apply the migration BEFORE the file upload.

## Step 1 (you) — apply the migration to `db58983`
SSMS / Azure Data Studio → Server `db58983.databaseasp.net`, Login `db58983`, Database `db58983` → open
`artifacts/migrate.sql` → Execute. The DB is at `AddChat`, so it applies only the two migrations above.

## Step 2 (agent) — FTPS upload
`app_offline.htm` up → upload the 4 changed `Cinora.*.dll` + the current `/dist` assets (+`.br`/`.gz`) +
`manifest.json` + `sw.js` → SHA-256 verify → remove `app_offline.htm`. `App_Data/` + `appsettings.Production.json`
preserved. (The DP-keys switch means `App_Data/keys` is no longer used, but leave it — harmless.)

## Step 3 — smoke test
1. `/health` → 200 Healthy. 2. Sign in (you'll be asked to, once) → land on Discover; refresh/close-reopen → still
signed in. 3. `/chat` opens; send a message; the emoji picker looks polished; times match your clock. 4. Open a
movie → **Send to a friend** → pick a friend → the movie card appears in the thread. 5. (Two devices / with push
granted) a new message raises a system notification that deep-links to the chat.

## After deploy
- **Rotate** the `db58983` + FTP passwords (shared in chat). Delete throwaway test accounts.

_Prepared 2026-07-15. Agents do not run git; a prod deploy needs your explicit go-ahead + credentials._
