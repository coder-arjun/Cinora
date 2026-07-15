# Re-deploy checklist — In-App Chat (1:1 + group + emoji + typing + read receipts) (2026-07-15)

An **update** to the already-live MonsterASP.NET app (`site78342.siteasp.net`, DB `db58983`). Full mechanics in
[`monsterasp-filezilla-deploy.md`](monsterasp-filezilla-deploy.md); this file adds only what's different for THIS
change. **The app never auto-migrates — Step 1 is run by hand.**

## What changed in this build
- **In-app chat** — a `/chat` two-pane messenger: 1:1 and group conversations, a built-in emoji picker, typing
  indicators, read receipts ("Seen"), group management (create / add / remove / leave / rename), a "Message"
  button on friend cards, and a nav chat entry + unread badge. Friends-only + blocking-aware; realtime over the
  existing self-hosted SignalR (new `/hubs/chat`). Design: ADR 0024.
- **Post-login redirect** now goes to **Discover** (was `/home`) — the change you asked for earlier.
- **Schema:** ONE new additive migration — **`AddChat`** (three brand-new EMPTY tables `Conversations`,
  `ConversationMembers`, `Messages` + their indexes + `Restrict` FKs to `Users`). No change to any existing table.

## Pre-built artifacts (ready in this repo)
| Artifact | Path |
|---|---|
| Deployable app (framework-dependent, frontend built, `chat.js` chunk present) | `./publish/` |
| Idempotent schema script (now incl. `AddChat`) | `./artifacts/migrate.sql` |

## Step 1 — Apply the migration to `db58983` (BEFORE uploading the app)
1. Connect to the MonsterASP MSSQL DB remotely (SSMS / Azure Data Studio): **Server** `db58983.databaseasp.net`,
   **User** `db58983`, **Password** (from the panel / your `appsettings.Production.json`), **Database** `db58983`.
2. **Select `db58983`** in the query window (triple-check it isn't another project's DB).
3. Open `artifacts/migrate.sql` and **Execute**. It's idempotent — applies only migrations not already in
   `__EFMigrationsHistory`. This DB is already at `AddUserBlock` (post-Phase-6), so the ONLY thing it applies is
   **`AddChat`**, creating the three empty chat tables.
   - The Phase-6 `Devices` unique-index caveat does **not** apply here — that migration is already in this DB.

## Step 2 — Upload the app
Upload the **contents of `./publish/`** into the site web root (per the FileZilla guide §6 — confirm the exact
document root in Plesk; it is the same root you used for the last deploy). **Re-deploy — PRESERVE on the server,
do NOT delete/overwrite:**
- **`appsettings.Production.json`** — your secrets (connection string, TMDB/Groq keys, `FileStorage:UrlSigningKey`).
  It is not in `./publish/`… actually it IS present in `./publish/` in this repo with real values — if you upload
  the whole folder it will overwrite the server copy with the same values, which is fine, but double-check the
  values match the panel before/after.
- **`App_Data/`** — Data-Protection keys (keeps users logged in) + avatar uploads. Keep it.

## Step 3 — Smoke test (in the deployed browser)
1. `https://<your-domain>/health` → **200 Healthy** (connection string + migration good).
2. Sign in → you land on **Discover** (the post-login redirect change).
3. Click the **chat icon** in the top bar → `/chat` opens the two-pane messenger (no 500 — the chat tables exist).
4. Open a friend's card → **Message** → a DM opens; send a message; the **😊 emoji picker** inserts an emoji.
5. Create a **group** (New group → pick friends), send to it.
6. (Two browsers) A sends → B sees it live; B typing shows "… is typing"; A's message flips to **Seen** when B reads.

## After deploy
- **Rotate** the DB + FTP passwords shared in chat earlier (the DB password is also sitting in
  `./publish/appsettings.Production.json` in the repo — treat that file as secret; do not commit `./publish/`).
- Delete any throwaway test accounts created while smoke-testing.
- The two-browser realtime smoke (step 6) is the one manual check that automated tests can't cover.

_Prepared 2026-07-15. Agents do not run git; a prod deploy needs your explicit go-ahead + the FTP/panel credentials._
