# Cinora → MonsterASP.net deployment (FileZilla / FTP)

A step-by-step for publishing Cinora to a **MonsterASP.net** site (free tier is fine — no Docker, no paid
Azure), uploading with **FileZilla**, on a **dedicated database** so your two other projects are untouched.

> **Golden rule for your other two projects:** Cinora gets its **own new MSSQL database**. It never touches the
> shared one, so there is **zero risk** to the other apps. See §1 for why sharing is unsafe.

---

## 0. What is already prepared in this repo

| Artifact | Path | Purpose |
|---|---|---|
| Deployable app | `./publish/` | Framework-dependent publish (built with the frontend). Upload its **contents**. |
| IIS host config | `./publish/web.config` | Already set to **Production** + first-boot stdout logging. |
| Prod config template | `./publish/appsettings.Production.json` | Fill in the connection string + keys (§4). |
| Schema script | `./artifacts/migrate.sql` | Idempotent; **regenerated & current** (incl. display-name/language). Run once (§5). |

The publish is **framework-dependent** — it runs `dotnet Cinora.Web.dll`, so the server must have the **.NET 10
ASP.NET Core Runtime**. Confirm that in §3; if it is missing, use the self-contained fallback in §10.

---

## 1. Use a NEW, dedicated database (do not share)

Cinora creates tables that **collide** with typical ASP.NET apps:

- **ASP.NET Identity:** `AspNetUsers`, `AspNetRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, …
- **Hangfire:** an entire `[HangFire]` schema (job queue tables), created automatically at startup.
- Cinora domain tables: `Users`, `Movies`, `Reviews`, `Watchlists`, `Notifications`, `Devices`, …

Your other two projects almost certainly have their **own** `AspNetUsers` (and maybe their own Hangfire) in the
shared DB. Putting Cinora there would collide on those names and could corrupt or lock their data. A **separate
database** isolates everything and is the only safe option.

**MonsterASP free tier** gives a limited number of MSSQL databases. If you can add another database, do it. If your
plan caps databases and the one you have is already used by the two projects, add a second database (small plan
bump) or a second free site/DB — **do not** reuse the shared one.

---

## 2. Create the site + database in the MonsterASP control panel (Plesk)

1. Sign in to the MonsterASP control panel.
2. **Website:** create/confirm your Cinora site. You get a free subdomain (e.g. `https://yourname.runasp.net`) with
   **free SSL** — or attach a custom domain later.
3. **Database → Add Database (MSSQL):** create a **new** database + a DB user, and note:
   - **Server / host** (e.g. `dbXXXX.<...>` shown in the panel)
   - **Database name**
   - **User id** and **Password**
   - The panel usually shows a ready **ADO.NET connection string** — copy it; you will paste it in §4.

---

## 3. Confirm the .NET runtime (framework-dependent vs self-contained)

In Plesk → your domain → the **.NET / Dotnet** settings, check which .NET versions are offered.

- **.NET 10 is available** → the prepared `./publish/` works as-is. Continue.
- **.NET 10 is NOT available** → jump to **§10** and republish **self-contained** (bundles the runtime), then come
  back. Everything else in this guide is identical.

---

## 4. Fill in `appsettings.Production.json`

Edit `./publish/appsettings.Production.json` (locally before upload, or on the server after upload) and replace the
placeholders. **Secrets live only in this file on the server — never in git, never in the base `appsettings.json`.**

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=<panel-host>;Database=<db-name>;User Id=<db-user>;Password=<db-pass>;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
  },
  "Tmdb":        { "ApiKey": "<your TMDB v4 read access token>" },
  "Ai":          { "Provider": "Groq", "Endpoint": "https://api.groq.com/openai/v1", "Model": "llama-3.3-70b-versatile", "ApiKey": "<your gsk_ Groq key>" },
  "FileStorage": { "UrlSigningKey": "<random 32+ byte base64 — REQUIRED>" }
}
```

- **Connection string:** paste the one from the MonsterASP panel. If you get a TLS/cert error on first boot, ensure
  `TrustServerCertificate=True` (and, if still failing, try `Encrypt=False`).
- **Copy your existing keys** (TMDB + Groq) from dev user-secrets:
  ```powershell
  dotnet user-secrets list --project src/Cinora.Web    # shows Tmdb:ApiKey and Ai:ApiKey values
  ```
- **`FileStorage:UrlSigningKey` is REQUIRED in Production** — the app aborts startup without it. Generate one:
  ```powershell
  [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
  ```
- **Optional (add later):** Google sign-in (`GoogleAuth:ClientId` / `GoogleAuth:ClientSecret`, redirect URI
  `https://<your-domain>/signin-google`) and Web Push (`WebPush:Subject` / `PublicKey` / `PrivateKey`). Absent, the
  app runs fine — those features simply stay off.

---

## 5. Create the schema — run `migrate.sql` against the NEW database

The app **never auto-migrates**. Create the tables once, **before** the first real visit.

1. Connect to the MonsterASP MSSQL database **remotely** with **SSMS** or **Azure Data Studio**, using the server /
   user / password from §2 (MonsterASP allows remote SQL connections).
2. **Select the new Cinora database** in the query window — triple-check it is NOT your other projects' DB.
3. Open `artifacts/migrate.sql` and **Execute**. It is idempotent (safe to re-run) and creates all tables + indexes,
   including the display-name/login-handle and default-language columns.
4. On a fresh DB the `Devices` table is empty, so the unique-index pre-check in the runbook (§5.3) is a no-op.
5. Hangfire creates its own `[HangFire]` tables automatically on first app start — nothing to do.

---

## 6. Upload with FileZilla

1. Get the **FTP** credentials from the MonsterASP panel (FTP host, username, password).
2. FileZilla → **File → Site Manager → New Site**:
   - **Protocol:** FTP – File Transfer Protocol
   - **Host:** the FTP host from the panel · **Encryption:** *Require explicit FTP over TLS* (FTPS)
   - **Logon Type:** Normal · **User / Password:** from the panel · **Connect**.
3. **Remote web root:** open the site's document root — in Plesk this is usually **`/httpdocs`** (confirm under
   Hosting Settings → Document root).
4. Upload the **contents of `./publish/`** (not the `publish` folder itself) into the web root:
   - `web.config`, `Cinora.Web.dll`, all `*.dll`, `appsettings.json`, `appsettings.Production.json`, and the whole
     `wwwroot/` folder (includes `dist/`, `sw.js`, `manifest.webmanifest`, `icons/`, `og-image.png`).
   - ~230 files. FileZilla transfers binary automatically.
5. Make sure `appsettings.Production.json` (with your real values) is present in the web root.

---

## 7. First boot + verification

1. Browse **`https://<your-domain>/health`** → expect **200 `Healthy`** (this is a DB-connectivity probe — a green
   here means the connection string + `migrate.sql` are correct).
2. Browse the site root → the Cinora landing page loads; register a user; sign in (email/password needs no Google).
3. Share test: paste your site URL into WhatsApp — it should unfurl the **Cinora OG card** (the branded logo image).

**If something is wrong**, read `logs/stdout_*.log` in the web root via FileZilla (stdout logging is ON):

| Symptom | Cause | Fix |
|---|---|---|
| HTTP **500.30 / 500.31** | .NET 10 runtime missing on host | Self-contained republish → **§10** |
| **500** + DB error in log | Wrong connection string, or `migrate.sql` not run on this DB | Recheck §4/§5 |
| Startup aborts: *UrlSigningKey* | `FileStorage:UrlSigningKey` missing | Set it in `appsettings.Production.json` (§4) |
| Site loads, no CSS/JS | `wwwroot/` not fully uploaded | Re-upload the whole `wwwroot/` |

Once healthy, set `stdoutLogEnabled="false"` in `web.config` and re-upload (reduces overhead).

---

## 8. AI recommendations after deploy

Serving makes **zero** LLM calls — the model runs only in the nightly **Hangfire** precompute job (Groq). To
populate the *For You* rail, sign in as an admin and trigger the job from **`/jobs`**, or wait for the nightly run.
If Groq is unreachable, Cinora falls back to a heuristic recommender (never a 500).

---

## 9. Operational notes

- **Hangfire + idle shutdown:** free shared hosting sleeps the app when idle, so the nightly job only fires while the
  app is awake. Keep it warm with a free uptime pinger (e.g. cron-job.org / UptimeRobot) hitting `/health` every
  5–10 minutes.
- **Writable folders:** Data-Protection keys (`App_Data/keys`), avatar uploads (`App_Data/uploads`), and Serilog
  (`logs/`) are created under the site folder, which is writable on MonsterASP. Persisting `App_Data/keys` keeps
  users logged in across restarts — **don't delete `App_Data/` on re-deploys**.
- **Re-deploys:** re-upload the `./publish/` contents, but **keep** `appsettings.Production.json` and `App_Data/` on
  the server. If you re-run `dotnet publish`, re-apply the `web.config` Production env-var block (or keep your edited
  `web.config`).
- **HTTPS:** MonsterASP provides free SSL; the app's secure cookies + HTTPS redirect + HSTS work behind IIS. IIS/ANCM
  forwards the real scheme + client IP, so rate-limiting keys correctly without extra proxy config.
- **Other two projects:** untouched — separate database (no shared tables) and a separate site folder.

---

## 10. Fallback — self-contained publish (only if .NET 10 is not on the host)

Bundles the .NET 10 runtime into the app so the host version no longer matters:

```powershell
Remove-Item -Recurse -Force .\publish-sc -ErrorAction SilentlyContinue
dotnet publish src/Cinora.Web -c Release -r win-x64 --self-contained true -o ./publish-sc
```

Then edit `./publish-sc/web.config` — the SDK will have written `processPath=".\Cinora.Web.exe" arguments=""`; add
back the Production env-var block:

```xml
<aspNetCore processPath=".\Cinora.Web.exe" arguments="" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
  </environmentVariables>
</aspNetCore>
```

Upload the contents of `./publish-sc/` instead of `./publish/`. (Self-contained is `win-x64`; MonsterASP app pools
are 64-bit. If the process fails to start on a 32-bit pool, switch `hostingModel` to `outofprocess` or republish
`-r win-x86`.)

---

_Prepared 2026-07-08. The app never auto-migrates; secrets stay in `appsettings.Production.json` on the server only._
