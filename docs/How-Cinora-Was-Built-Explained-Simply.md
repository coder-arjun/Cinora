# How Cinora Was Built — Explained Simply (with code)

*A friendly, plain-English walkthrough of how the Cinora app is put together — with real code from the project. No prior knowledge of the codebase assumed.*

---

## 1. What is Cinora?

**Cinora is a premium, social movie-and-series review app**, delivered as a **Progressive Web App (PWA)** — meaning it runs in the browser but can be *installed* like a real app and even works offline.

What can you do in it?

- **Rate and review** movies and shows, and read reviews.
- **Add friends**, see a feed of your friends' reviews, and **like/comment**.
- **Block** people you don't want to interact with (they can't find you, message you, or see your profile).
- **Search** for people by their exact username or email.
- **Chat** with friends one-to-one or in **groups** — with emojis, typing indicators, "delivered/read" ticks, and the ability to **share a movie** straight into a conversation.
- Build **watchlists**, get **AI-powered recommendations**, and receive **notifications** (even push notifications).

A guiding rule for the whole project: **everything is free and runs locally** — no paid cloud services, no Docker. Wherever a "normal" app would reach for a paid service, Cinora uses a free equivalent (more on that in §3).

---

## 2. The big idea: "Clean Architecture" (think of it like a company)

The hardest part of a real app isn't writing code — it's keeping it **organized** so it doesn't turn into spaghetti. Cinora uses a well-known layout called **Clean Architecture**. The easiest way to picture it is as a **company with four departments**, arranged in rings from the inside out:

| Layer (project) | Think of it as… | Its job |
|---|---|---|
| **Domain** | The **company rulebook** | The core business rules and "smart objects" (a `User`, a `Review`, a `Friend`). Knows nothing about databases or the web. |
| **Application** | The **managers / workflows** | Coordinates *what* should happen ("send a friend request", "block a user"). Doesn't know *how* data is stored. |
| **Infrastructure** | The **warehouse & outside vendors** | The actual database, file storage, the TMDB movie API, the AI model. The "how". |
| **Web** | The **front desk** | The website itself — pages, buttons, security. Talks to visitors. |

**The one golden rule:** inner rings never depend on outer rings. The rulebook (Domain) doesn't know the database exists. The managers (Application) don't know whether we use SQL Server or a text file. This is what keeps the app changeable and testable.

In Cinora this rule isn't just a suggestion — it's **enforced by the compiler**. Each layer is a separate C# project, and the project references only point *inward*:

```
Cinora.Domain          → (references nothing)
Cinora.Application     → Domain
Cinora.Infrastructure  → Application
Cinora.Web             → Application + Infrastructure
```

If someone accidentally tries to use the database directly from the Domain layer, **the project won't even compile.** The architecture defends itself.

### A closer look at the four projects

Each "department" is one C# project with a clear job:

- **`Cinora.Domain`** — the pure heart: the entities (like `Friend`, `Review`, `Conversation`), a couple of **value objects** (see §4), and the enums. No database, no web, no third-party libraries. It could be lifted into a completely different app unchanged.
- **`Cinora.Application`** — **one folder per feature** (`Friends`, `Chat`, `Reviews`, `Watchlist`, `Recommendations`, `Notifications`, …), each holding its commands, queries, handlers, and validators. This layer also *defines the interfaces* ("ports") for anything it needs from the outside world — without knowing how they're implemented.
- **`Cinora.Infrastructure`** — the concrete "how": the EF Core database and one **adapter** per port (the real file storage, the TMDB client, the AI engine, the Web-Push sender, and so on).
- **`Cinora.Web`** — the visible app: controllers, Razor pages, the SignalR chat/notification hubs, the security wiring, and the **composition root** — the one place that decides "when someone asks for `IFileStorage`, hand them the local-filesystem one."

### Ports and adapters — the swappable seams

The single most important habit in the codebase: the core talks to **interfaces**, and the real implementation is plugged in at the edge. This is why the "free-only" rule (§3) costs almost nothing — swapping a provider is a one-line change in the composition root and touches nothing else. A sample of the real ports:

| Port (what the app asks for) | Today's free adapter |
|---|---|
| `IAppDbContext` — the database | EF Core over SQL Server |
| `IFileStorage` — save/serve avatars | Local filesystem (a stand-in for cloud blob storage) |
| `ITmdbClient` — movie data | A typed HTTP client, wrapped by a caching decorator |
| `IRecommendationEngine` — AI picks | Groq / Ollama behind `Microsoft.Extensions.AI` |
| `IRealtimeNotifier` / `IChatNotifier` — live pushes | Self-hosted SignalR |
| `IPushSender` — mobile push | A free VAPID Web-Push library |
| `ICurrentUser` — "who is acting" | Reads the secure login cookie on the server |

There are about **15** such ports in total. Whenever you see an `I…`-named type in the app's core, it's almost always a seam like this.

### Every big decision is written down (ADRs)

Cinora keeps a folder of **Architecture Decision Records** — **24** so far, in `docs/adr/`. Each one captures a real fork in the road: *what* was decided, *why*, and *what was rejected*. A few examples: a hand-rolled mediator because the popular library went commercial (ADR 0005); the free-only, no-Docker rule (ADR 0004); enforcing "you can only edit your own stuff" inside each handler (ADR 0009); modelling blocking as its own entity (ADR 0022); and the chat privacy model (ADR 0024). If you ever wonder "why is it done *this* way?", the answer is usually in an ADR.

---

## 3. The tech stack (and the "free-only" trick)

Cinora is built on **ASP.NET Core 10** (Microsoft's web framework) with **C#**. Here's the stack, and the clever free substitutions:

| What a big app usually pays for | What Cinora uses instead (free) |
|---|---|
| Azure SQL database | **SQL Server LocalDB / Express** (free, on your machine) |
| Redis cache | **In-memory cache** (built into .NET) |
| Azure Blob storage (for avatars) | **The local file system**, behind a swappable interface |
| Paid OpenAI | **Ollama** (local AI) or **Groq's free tier** for recommendations |
| Azure SignalR (real-time) | **Self-hosted SignalR** (free, built in) |
| A movie database | **TMDB's free API** |

The trick that makes this possible is **interfaces (called "ports")**. The app's core talks to an *interface* like `IFileStorage` or `IRecommendationEngine`, not to a specific vendor. Today it's wired to the free version; swapping in a paid one later is a one-line change and touches nothing else. You'll see this pattern everywhere.

---

## 4. How data is modeled: "smart objects" that protect their own rules

A common beginner mistake is to make data objects that are just bags of fields, and then scatter the rules for changing them all over the app. Cinora does the opposite: **each entity protects its own rules.** You can't put it into an invalid state.

Here's the real `Friend` entity (a friendship request between two people):

```csharp
public sealed class Friend
{
    private Friend() { }                       // can't be created any old way

    public Guid Id { get; private set; }
    public Guid RequesterId { get; private set; }
    public Guid AddresseeId { get; private set; }
    public FriendStatus Status { get; private set; }   // Pending / Accepted / Declined

    // The ONLY way to make a friend request — and it refuses a self-request.
    public static Friend Request(Guid requesterId, Guid addresseeId)
    {
        if (requesterId == addresseeId)
            throw new DomainException("A user cannot send a friend request to themselves.");

        return new Friend { Id = Guid.NewGuid(), RequesterId = requesterId,
                            AddresseeId = addresseeId, Status = FriendStatus.Pending,
                            RequestedAtUtc = DateTime.UtcNow };
    }

    public void Accept()  => TransitionTo(FriendStatus.Accepted);
    public void Decline() => TransitionTo(FriendStatus.Declined);

    private void TransitionTo(FriendStatus status)
    {
        if (Status != FriendStatus.Pending)     // can't accept an already-accepted request
            throw new DomainException("Only a pending friend request can be accepted or declined.");
        Status = status;
        RespondedAtUtc = DateTime.UtcNow;
    }
}
```

Notice: the fields are `private set` (nobody outside can just overwrite them), and the only ways to create or change a `Friend` are through methods that **check the rules first**. A friendship can't accidentally befriend yourself, or be "accepted" twice.

The **blocking** feature added a brand-new entity in exactly the same spirit — a `UserBlock`:

```csharp
public sealed class UserBlock
{
    public static UserBlock Create(Guid blockerId, Guid blockedUserId)
    {
        if (blockerId == blockedUserId)
            throw new DomainException("A user cannot block themselves.");
        return new UserBlock { Id = Guid.NewGuid(), BlockerId = blockerId,
                               BlockedUserId = blockedUserId, CreatedAtUtc = DateTime.UtcNow };
    }
}
```

Blocking is kept **separate** from friendship on purpose: you can block someone you were never friends with, and blocking has none of the "pending/accepted" lifecycle a friendship does. Keeping them separate keeps both simple.

### The full cast — every entity in Cinora

The whole app is built from just **16 entities**, each a "smart object" like the two above. Grouped by what they're for:

| Area | Entities |
|---|---|
| **People & identity** | `User` (the profile / social side) — paired with the login-side `ApplicationUser` (see §6) |
| **The social graph** | `Friend` (a directed friendship/request), `UserBlock` (a one-way block) |
| **The movie catalogue** | `Movie` (a cached title), `Genre`, `MovieGenre` (the link between them) |
| **Reviews & interaction** | `Review` (a rating + text), `ReviewLike`, `Comment` |
| **Watchlists** | `Watchlist` (a title on your list, with a status) |
| **Notifications & push** | `Notification` (an inbox item), `Device` (a registered push subscription) |
| **AI** | `AIRecommendationHistory` (an audit trail of generated picks) |
| **Chat** | `Conversation`, `ConversationMember`, `Message` |

That's the entire model. Everything you can do in the app is some combination of these sixteen objects changing state through their own guarded methods.

### Value objects — tiny types that are always valid

Some concepts aren't "things with an identity" — they're just **values**. Cinora models two as **value objects**, which can *never* hold an invalid value:

- **`Rating`** — a movie score. It's created only through `Rating.From(value)`, which enforces a **1–10** range; there is simply no way to construct a rating of 42. Two ratings are "equal" when their numbers match.

```csharp
public static Rating From(int value)
{
    if (value is < MinValue or > MaxValue)          // MinValue = 1, MaxValue = 10
        throw new DomainException("Rating must be between 1 and 10.");
    return new Rating(value);
}
```

- **`NotificationPreferences`** — four on/off switches (one per notification type) that live *inside* the `User`. Asking "should I push a friend-request alert to this person?" is a single call, `IsPushEnabled(type)`, with the rule kept in one place.

### Enums — the fixed vocabularies

Finally, six **enums** name the small fixed sets the domain speaks in: `FriendStatus` (Pending / Accepted / Declined), `WatchlistStatus` (PlanToWatch / Watching / Watched), `MediaType` (Movie / Series), `NotificationType`, and — new with chat — `ConversationType` (Direct / Group) and `ConversationRole` (Member / Admin). Using named values instead of loose strings or numbers means the compiler catches typos, and the code reads like plain English.

---

## 5. How a request flows: commands, queries, and a tiny "mediator"

When you click **"Block"** in the app, a lot has to happen: check you're allowed, create the block, remove any existing friendship, save it — all safely. Cinora organizes this with a pattern called **CQRS** (Command Query Responsibility Segregation), which is a fancy name for a simple idea:

- A **Command** *changes* something ("Block this user"). 
- A **Query** *reads* something ("Get my blocked list").

Each one is a small, self-contained message with a **handler** that does the work. A lightweight dispatcher called `ISender` finds the right handler for each message. (Cinora uses a *hand-rolled* mediator — about 100 lines of its own code — because the popular library for this, MediatR, went commercial, and the project is free-only.)

Here's the real handler for blocking someone. Read the comments — it tells a little story:

```csharp
public sealed class BlockUserCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<BlockUserCommand, Unit>
{
    public async Task<Unit> Handle(BlockUserCommand request, CancellationToken ct)
    {
        var me     = currentUser.GetRequiredUserId();   // WHO is acting — resolved on the server, never trusted from the browser
        var target = request.TargetUserId;

        if (!await db.Users.AnyAsync(u => u.Id == target, ct))
            throw new NotFoundException($"User ({target}) was not found.");

        // Already blocked? Do nothing (idempotent — clicking twice is harmless).
        if (await db.UserBlocks.AnyAsync(b => b.BlockerId == me && b.BlockedUserId == target, ct))
            return Unit.Value;

        db.UserBlocks.Add(UserBlock.Create(me, target));   // the entity enforces "no self-block"

        // Full cut-off: remove any friendship or pending request between the two, either direction.
        var pair = await db.Friends
            .Where(f => (f.RequesterId == me && f.AddresseeId == target)
                     || (f.RequesterId == target && f.AddresseeId == me)).ToListAsync(ct);
        db.Friends.RemoveRange(pair);

        await db.SaveChangesAsync(ct);   // all of the above saved together, in one go
        return Unit.Value;
    }
}
```

Two things worth calling out for beginners:

1. **`ICurrentUser.GetRequiredUserId()`** — the app figures out *who you are* from your secure login cookie on the **server**. It never lets the browser say "I am user X". That single habit prevents a whole category of "act as someone else" attacks.
2. **It's a small, focused unit.** This handler does *one* thing. It's easy to read, easy to test, and it can't be reached without going through the security checks first.

---

## 6. Talking to the database (EF Core, and a neat "two-row user" trick)

Cinora uses **Entity Framework Core (EF Core)** — a tool that lets you work with the database using normal C# objects instead of writing SQL by hand. The Application layer only ever talks to an interface, `IAppDbContext`, so it never has to know it's SQL Server underneath.

A nice detail: a Cinora user is actually **two rows** that share the same ID:

- an **`ApplicationUser`** row (handled by ASP.NET's login system — holds the password, email, etc.), and
- a **`User`** row (the app's own profile/social data — display name, avatar, settings).

They're created together, atomically, when you register. This keeps *login/security* concerns and *app* concerns cleanly separated while still being "one user".

Reads are done as **projections** — the app asks the database for *exactly* the fields it needs and no more, which is fast:

```csharp
var owner = await db.Users.AsNoTracking()
    .Where(u => u.Id == ownerId)
    .Select(u => new { u.DisplayName, u.AvatarFileKey, u.IsProfilePublic })  // just these 3 columns
    .FirstOrDefaultAsync(ct);
```

---

## 7. Getting movie data — and a real "war story" about images

Movie data (titles, posters, cast) comes from **TMDB**, a free movie database API. Cinora wraps it in a typed client and **caches** the results so it doesn't call TMDB over and over for the same movie.

The most instructive part of the whole project is the **movie-poster saga**, because it shows how real debugging works:

- **The problem:** on the live site, movie titles showed up but **poster images were blank**.
- **First guess (wrong):** originally, images were fetched by *Cinora's own server* (a "proxy") and passed to the browser. On the free host, the server couldn't reach TMDB's image servers, so every poster fell back to a placeholder. So we changed it to let the **browser** load images straight from TMDB.
- **Still broken:** the browser *also* couldn't load TMDB's image server — it turns out **TMDB's image CDN (`image.tmdb.org`) is blocked on some networks/regions.** So neither the server nor the browser could reach it.
- **The fix that worked:** route posters through a **free public image proxy, `images.weserv.nl`**, which fetches the image from TMDB on *its* servers and serves it from a globally-reachable CDN. It even auto-optimizes to WebP (smaller files).

The final code that builds a poster URL is tiny:

```csharp
public string? Build(string? path, TmdbImageSize size)
{
    if (string.IsNullOrWhiteSpace(path)) return null;
    var tmdbUrl  = $"{_imageBaseUrl}/{ToSizeToken(size)}{Normalize(path)}"; // https://image.tmdb.org/t/p/w342/abc.jpg
    var upstream = tmdbUrl.Replace("https://", "");                          // image.tmdb.org/t/p/w342/abc.jpg
    return $"https://images.weserv.nl/?url={upstream}";                      // browser → weserv → TMDB
}
```

**Lesson:** the same symptom ("no images") had *two* different causes stacked on top of each other. You fix real bugs by *checking each assumption with evidence* (we literally inspected the live page's HTML and tested the URLs), not by guessing.

---

## 8. Security — how the app stays safe

Security in Cinora is "**closed by default**". A few of the key ideas, in plain terms:

- **Fail-closed authorization.** Every page requires you to be logged in *unless it's explicitly marked public* (the landing page, the login page, the health check). So forgetting to add a security check doesn't accidentally expose a page — the opposite of most apps.
- **Anti-forgery tokens.** Every action that changes data carries a secret token proving the request came from Cinora's own pages, not a malicious site.
- **A strict Content-Security-Policy (CSP).** This browser header says "only load scripts/styles/images from places I trust." It's why adding the image proxy required one deliberate, reviewed line:

```
img-src 'self' https://image.tmdb.org https://images.weserv.nl data:;
```

- **Blocking never leaks.** If someone blocked you, searching for them and searching for a person who doesn't exist return the **exact same empty result** — so you can never tell whether you were blocked. The rule lives in one small, shared helper so it can't be applied inconsistently.
- **Escaping user text.** Anything a user typed (a display name, a review) is automatically HTML-escaped when shown, so nobody can sneak a `<script>` into a review.
- **Rate limiting.** Sensitive actions (login, search, social writes) are throttled to blunt spam and abuse.

---

## 9. Real-time notifications (SignalR)

When a friend likes your review, you get a **live toast notification** without refreshing. That's powered by **SignalR** (self-hosted, free), which keeps a live connection open between the browser and the server.

The important design choice: the notification is **saved to the database first** (so your inbox is always correct), and *then* pushed live as a "best-effort" extra. If the live push fails, you simply see it next time you look — you never *lose* a notification. Reliability first, real-time second.

---

## 10. In-app chat — the newest feature (and two debugging war stories)

Cinora now has a full **chat** system: one-to-one and **group** conversations, a built-in **emoji picker**, **typing indicators**, WhatsApp-style **delivery/read ticks**, the ability to **share a movie** into a chat, and **mobile push notifications** for new messages. It's built entirely on the free, self-hosted SignalR from §9 — no new paid pieces.

**Privacy is the core rule: a chat stays between its participants.** There's no separate "permissions" table — instead, *being a member of the conversation IS your permission*. Every read and write re-checks membership on the server, so a non-member asking for a conversation's history simply gets a "403 Forbidden":

```csharp
var members = await db.ConversationMembers
    .Where(m => m.ConversationId == conversationId)
    .Select(m => m.UserId).ToListAsync(ct);

if (!members.Contains(me))                              // am I actually in this chat?
    throw new ForbiddenAccessException("You are not a member of this conversation.");
```

A security review of the chat caught (and we fixed) three subtle issues worth understanding:

- **A removed group member could still receive new live messages** until they refreshed — the saved history correctly said "no", but the *live* connection was still attached. The fix: push each new message only to the conversation's **current** members, looked up fresh at send time, so a removed person is never on the list.
- **A newly-added group member could scroll up and read history from before they joined.** The fix: only show a member messages sent **after** they joined.
- **The blue "read" ticks** should require the recipient to actually *view* the chat — a background browser tab counts as "delivered", not "read".

The **ticks** work just like the messengers you know: one grey tick = *sent*, two grey ticks = *delivered to their device*, two **blue** ticks = *read*. The "read" state is worked out from a simple fact the app already tracks — how far each person has read — so no extra bookkeeping is needed.

### War story #1: "It keeps logging me out"

On the live site, users were signed out constantly. The cause was subtle: the secret keys that encrypt the login cookie were stored **on the server's hard drive**, and the free host **wipes that folder on every redeploy** (and even between idle restarts). New keys ⇒ every existing login cookie is suddenly unreadable ⇒ everyone is logged out.

The fix (borrowed from two sibling projects): **store those keys in the database** instead — durable across restarts and redeploys — and make every login **"remember me" by default**, so the cookie survives closing the app. Now you stay signed in.

### War story #2: "The chat times are wrong"

Chat message times showed the wrong hour. The bug: the server sent the timestamp as text **without a timezone marker** (a trailing `Z` meaning "UTC"). A browser that sees no timezone assumes the text is *local time* — so no conversion happened and everyone saw UTC. The fix is to make the timestamp explicitly say "this is UTC", and convert it to the viewer's own time in the browser:

```js
// A timestamp with no zone must be treated as UTC, then shown in the viewer's local time.
const hasZone = /[zZ]$/.test(iso) || /[+-]\d\d:?\d\d$/.test(iso);
const local = new Date(hasZone ? iso : iso + "Z").toLocaleTimeString();
```

**Lesson (again):** both bugs looked mysterious but had precise, boring causes — found by reasoning about *exactly* what the computer was doing, not by guessing (just like the poster saga in §7).

---

## 11. AI recommendations — done the safe way

Cinora recommends titles using an AI model, but with three guardrails that make it trustworthy and free:

1. **Zero AI calls happen while you browse.** The model runs in a **nightly background job** (Hangfire), which precomputes your picks and stores them. Serving your "For You" rail just reads the stored result — instant, and free of per-request AI cost.
2. **A "hallucination guard".** The AI is only allowed to pick from a **real list of candidate movies** we hand it — it can't invent a film that doesn't exist.
3. **It never crashes the page.** If the AI is down, a simple non-AI heuristic fills in instead. The recommendations rail *cannot* return an error.

Again, the model sits behind an interface (`IRecommendationEngine`), so today's free Groq/Ollama can be swapped for anything later.

---

## 12. Making it a PWA (installable + offline)

Two files turn Cinora into an installable, offline-capable app:

- A **web app manifest** (name, icons, colors) so phones/desktops can "install" it.
- A **service worker** — a tiny script that sits between the app and the network and can serve cached files when you're offline.

The service worker uses sensible strategies: it always tries the **network first** for pages (so you get fresh content), caches the app's own hashed CSS/JS, and serves a branded **offline page** if you truly have no connection. It's also *versioned*, so when a new version deploys, old caches are cleaned up.

---

## 13. The front end — server-rendered, sprinkled with interactivity

Cinora deliberately **isn't** a heavy single-page JavaScript app. Pages are rendered on the server with **Razor** (HTML templates with C#), styled with **Tailwind CSS v4** (a "dark luxury glassmorphism" theme), and made interactive with two lightweight tools:

- **HTMX** — lets a button swap in a piece of HTML from the server without a full page reload (e.g., clicking "Add friend" replaces just that button with a "Requested" state). No custom JavaScript needed.
- **Alpine.js** — small sprinkles of interactivity (dropdowns, dialogs) written right in the HTML.

For example, the friend-search box is *pure HTML attributes* — as you type, it asks the server and swaps in the result, with no hand-written JavaScript:

```html
<input type="search" name="term" placeholder="Exact username or email"
       hx-get="/friends/search"
       hx-trigger="input changed delay:400ms"
       hx-target="#user-search-result" />
<div id="user-search-result" aria-live="polite"></div>
```

This keeps the app fast, simple, and accessible.

---

## 14. How it's built, tested, and shipped

- **Building:** one command compiles everything; the front-end assets (CSS/JS) are bundled and given content-hashed names so browsers cache them safely.
- **Testing:** the project has **546 automated tests** across four test projects (domain rules, application workflows, infrastructure, and full end-to-end web tests that drive the real app through a browser-like client). They must all pass before shipping. The web tests run against a *real* database, not a fake one, so they catch real integration bugs.
- **Deploying:** the app is published and **uploaded over FTP** to a free Windows host (MonsterASP.NET). The database schema is applied with a hand-reviewed, idempotent SQL script — the app **never** changes the database automatically on startup (that avoids risky surprises). During an update, an `app_offline` file briefly takes the site down so the running files can be safely replaced, then it's removed to bring the new version live. Each uploaded file is **verified by checksum** to be sure it transferred perfectly.

---

## 15. The big takeaways

If you remember five things about how Cinora is built:

1. **Layers with a one-way dependency rule**, enforced by the compiler, keep the code from turning into spaghetti.
2. **Entities protect their own rules** — you can't put data into an invalid state.
3. **Every change is a small, focused, testable command**, and the server (never the browser) decides who you are.
4. **Everything talks to interfaces**, so free tools can be swapped for paid ones later with zero ripple.
5. **Security is closed-by-default**, and real bugs are solved with **evidence**, not guesses (the poster saga is the proof).

That combination is what lets a feature-rich, secure, real-time social app run entirely on **free, local tools** — and still be a pleasure to change.

---

*Document generated for the Cinora project. Code snippets are simplified for readability; the full source lives in the `src/` folder, and the architecture decisions behind each choice are recorded in `docs/adr/` and `docs/architecture/`.*
