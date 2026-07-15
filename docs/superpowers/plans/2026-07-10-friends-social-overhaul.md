# Friends & Social Overhaul — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix deployed movie thumbnails, then add user blocking (full cut-off + hide), exact-match user search, a redesigned find-friends hub with WhatsApp invite + in-app requests, cancel-outgoing, and authenticated-only access to the Cinora app.

**Architecture:** Extends the existing Clean-Architecture social slice (Domain `Friend`/`User` + `Cinora.Application/Features/Friends` verticals via the hand-rolled `ISender`, `IAppDbContext`, `ICurrentUser`). One new Domain entity (`UserBlock`), one new Application port (`IUserDirectory`), one additive EF migration. All new writes are `[Authorize]` HTMX partial endpoints under the existing `social-write` rate-limit policy. No CSP change.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core (SQL Server LocalDB `CinoraTest` for integration tests), hand-rolled mediator, FluentValidation, xUnit + NSubstitute, Razor + HTMX + Alpine, Tailwind v4.

**Source spec:** `docs/architecture/friends-social-overhaul-design.md` (section refs below like "§2" point at it).

## Global Constraints

*Every task's requirements implicitly include this section.*

- **NEVER run `git` (commit/push/tag/init/add) — human-only, enforced by `.claude/settings.json`.** Each task ends with a **Checkpoint** (build + test + review gates), NOT a git commit. The human reviews and commits.
- **FREE / LOCAL ONLY — no paid services, no Docker.** (spec governing constraints)
- **Hand-rolled mediator only** — `ISender`/`IRequest<T>`/`IRequestHandler<,>`/`Unit` from `Cinora.Application/Common/Messaging`. **Never add MediatR. Never add FluentAssertions** (tests use xUnit `Assert` + `NSubstitute`).
- **`TreatWarningsAsErrors` is ON** — a warning fails `dotnet build`. Full XML doc comments on public types/members (match the surrounding files).
- **Fail-closed authZ + global anti-forgery + strict CSP.** New writes are `[Authorize]` POST/DELETE carrying the token via the `RequestVerificationToken` header. **No CSP widening** (`img-src` already allows `https://image.tmdb.org`; `connect-src 'self'` covers HTMX).
- **No `Html.Raw` on user content** — the searched term, every `DisplayName`, and emails are Razor output-encoded.
- **Search is EXACT-MATCH only** (username or email); a no-match AND a blocked user both return an **empty** result (never reveal a block). Blocking is **silent**.
- **One additive migration** total (`AddUserBlock`); the app never auto-migrates — apply via `scripts/db-update.ps1` / the idempotent SQL script.
- Build/test commands run from repo root `D:\MyProject\Cinora\Cinora\Cinora` (PowerShell 7+).

**Checkpoint definition (used at the end of every task):**
```
dotnet build Cinora.sln -c Release          # 0 warnings / 0 errors (TWAE on)
dotnet test <the task's test project>       # the task's tests green
```
Then STOP and hand off to the human for review + commit. Do NOT run git. After the last task of a milestone (B0/B1/B2/B3/B4), also run the review gates `/review-security`, `/review-code`, and (for UI milestones) `/review-ui` per `CLAUDE.md`.

---

## Milestone B0 — Unit A: movie posters load directly from TMDB

Root cause (confirmed, spec intro): posters render through a same-origin proxy `/tmdb-img/{size}/{file}` (`TmdbImageController`) that fetches from `image.tmdb.org` **server-side**; the deployed free host can't reach TMDB outbound, so every poster 302s to the placeholder. Fix: emit direct `https://image.tmdb.org/...` URLs (the browser fetches them; CSP already allows the host), retire the proxy.

### Task B0.1: Repoint `TmdbImageTagHelper` to direct TMDB URLs; retire the proxy

**Files:**
- Modify: `src/Cinora.Web/TagHelpers/TmdbImageTagHelper.cs`
- Delete: `src/Cinora.Web/Controllers/TmdbImageController.cs`
- Test: `tests/Cinora.Web.IntegrationTests/TagHelpers/TmdbImageTagHelperTests.cs` (create)

**Interfaces:**
- Consumes: `ITmdbImageUrlBuilder.Build(string? path, TmdbImageSize size) -> string?` (existing port, `Cinora.Application/Common/Interfaces`; registered singleton in Infrastructure; produces `https://image.tmdb.org/t/p/{size}{path}` from `TmdbOptions.ImageBaseUrl`), `TmdbImageSize` (`Cinora.Application/Common/Tmdb`).
- Produces: an `<img>` whose `src` is an absolute `image.tmdb.org` URL (or a same-origin placeholder when the path is null/blank).

- [ ] **Step 1: Write the failing test**

Create `tests/Cinora.Web.IntegrationTests/TagHelpers/TmdbImageTagHelperTests.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Cinora.Web.TagHelpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.TagHelpers;

public sealed class TmdbImageTagHelperTests
{
    private static (TmdbImageTagHelper Helper, TagHelperOutput Output) Build(
        ITmdbImageUrlBuilder urlBuilder, string? poster)
    {
        // The URL-helper factory is used ONLY for the same-origin placeholder; echo its input back.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.Content(Arg.Any<string>()).Returns(call => (string)call[0]!);
        var factory = Substitute.For<IUrlHelperFactory>();
        factory.GetUrlHelper(Arg.Any<ViewContext>()).Returns(urlHelper);

        var helper = new TmdbImageTagHelper(factory, urlBuilder)
        {
            ViewContext = new ViewContext(),
            Poster = poster,
            Size = TmdbImageSize.W342,
        };

        var output = new TagHelperOutput(
            "img",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        return (helper, output);
    }

    [Fact]
    public void Emits_absolute_tmdb_url_and_not_the_proxy_path()
    {
        var urlBuilder = Substitute.For<ITmdbImageUrlBuilder>();
        urlBuilder.Build("/abc.jpg", TmdbImageSize.W342)
            .Returns("https://image.tmdb.org/t/p/w342/abc.jpg");
        var (helper, output) = Build(urlBuilder, "/abc.jpg");

        helper.Process(new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id"), output);

        var src = output.Attributes["src"].Value?.ToString();
        Assert.Equal("https://image.tmdb.org/t/p/w342/abc.jpg", src);
        Assert.DoesNotContain("/tmdb-img/", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_same_origin_placeholder_when_path_is_null()
    {
        var urlBuilder = Substitute.For<ITmdbImageUrlBuilder>();
        urlBuilder.Build(Arg.Any<string?>(), Arg.Any<TmdbImageSize>()).Returns((string?)null);
        var (helper, output) = Build(urlBuilder, null);

        helper.Process(new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id"), output);

        Assert.Equal("~/img/poster-placeholder.svg", output.Attributes["src"].Value?.ToString());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~TmdbImageTagHelperTests"`
Expected: FAIL — the current `TmdbImageTagHelper` constructor takes only `IUrlHelperFactory`, so this does not compile / the assertions fail (src is a `/tmdb-img/...` path).

- [ ] **Step 3: Rewrite the tag helper to use the URL builder**

Replace the body of `src/Cinora.Web/TagHelpers/TmdbImageTagHelper.cs` with (note: the `ProxyBasePath` constant, `BuildProxyPath`, and `ToSizeToken` are removed — the builder owns URL construction):

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Tmdb;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Cinora.Web.TagHelpers;

/// <summary>
/// Renders a TMDB image on an <c>&lt;img&gt;</c> element from a RAW TMDB path plus a requested size. Usage:
/// <c>&lt;img asp-tmdb-poster="@card.PosterPath" asp-tmdb-size="W342" alt="@card.Title"&gt;</c>. The <c>src</c>
/// points DIRECTLY at <c>https://image.tmdb.org/t/p/{size}{path}</c> (built by <see cref="ITmdbImageUrlBuilder"/>
/// from <c>TmdbOptions.ImageBaseUrl</c>) so the browser — which can reach TMDB's CDN — loads it, with no
/// server-side dependency. The CSP already allows <c>https://image.tmdb.org</c> in <c>img-src</c>. When the path
/// is <c>null</c>/blank the helper emits a same-origin placeholder instead of a broken image, and it always adds
/// <c>loading="lazy"</c> + <c>decoding="async"</c>. It never invents an <c>alt</c> — the caller supplies it.
/// </summary>
[HtmlTargetElement("img", Attributes = PosterAttributeName)]
public sealed class TmdbImageTagHelper : TagHelper
{
    private const string PosterAttributeName = "asp-tmdb-poster";
    private const string SizeAttributeName = "asp-tmdb-size";
    private const string PlaceholderPath = "~/img/poster-placeholder.svg";

    private readonly IUrlHelperFactory _urlHelperFactory;
    private readonly ITmdbImageUrlBuilder _imageUrlBuilder;

    /// <summary>Creates the tag helper.</summary>
    /// <param name="urlHelperFactory">Resolves the app-relative placeholder URL from the view context.</param>
    /// <param name="imageUrlBuilder">Builds the absolute <c>image.tmdb.org</c> URL from the raw path + size.</param>
    public TmdbImageTagHelper(IUrlHelperFactory urlHelperFactory, ITmdbImageUrlBuilder imageUrlBuilder)
    {
        _urlHelperFactory = urlHelperFactory;
        _imageUrlBuilder = imageUrlBuilder;
    }

    /// <summary>The RAW TMDB poster/image path (e.g. <c>/abc.jpg</c>), or <c>null</c> to render the placeholder.</summary>
    [HtmlAttributeName(PosterAttributeName)]
    public string? Poster { get; set; }

    /// <summary>The render size to request; defaults to <see cref="TmdbImageSize.W342"/> (rail/search cards).</summary>
    [HtmlAttributeName(SizeAttributeName)]
    public TmdbImageSize Size { get; set; } = TmdbImageSize.W342;

    /// <summary>The ambient view context, used to resolve the app-relative placeholder URL (not a bound attribute).</summary>
    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Direct absolute TMDB URL (browser fetches it). A null/blank path falls back to the same-origin
        // placeholder resolved through the app base, never a broken <img>.
        var absolute = _imageUrlBuilder.Build(Poster, Size);
        var src = absolute ?? _urlHelperFactory.GetUrlHelper(ViewContext).Content(PlaceholderPath);
        output.Attributes.SetAttribute("src", src);

        if (!output.Attributes.ContainsName("loading"))
        {
            output.Attributes.SetAttribute("loading", "lazy");
        }

        if (!output.Attributes.ContainsName("decoding"))
        {
            output.Attributes.SetAttribute("decoding", "async");
        }
    }
}
```

- [ ] **Step 4: Delete the now-unused proxy controller**

Delete `src/Cinora.Web/Controllers/TmdbImageController.cs`. (The service worker's `image.tmdb.org` cache-first branch in `src/Cinora.Web/Scripts/sw.ts` now becomes live again — that is a benefit; no change required. If a grep for `tmdb-img` finds any stray reference other than the deleted controller, fix it.)

Run: `dotnet build Cinora.sln -c Release` and grep to confirm no dangling references:
Run: `rg "tmdb-img" src` → expected: no results (or only inside `sw.ts` comments, which are harmless).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~TmdbImageTagHelperTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Checkpoint**

Run the Checkpoint (build + full `dotnet test tests/Cinora.Web.IntegrationTests/...`). Confirm `npm run build --prefix src/Cinora.Web` is still clean and the CSP is byte-unchanged (`/review-security`). Manual (deployed): posters render on the live MonsterASP.NET app. Hand off to the human for commit — do NOT run git.

---

## Milestone B1 — Blocking foundation (ADR 0022)

### Task B1.1: `UserBlock` domain entity

**Files:**
- Create: `src/Cinora.Domain/Entities/UserBlock.cs`
- Test: `tests/Cinora.Domain.Tests/Entities/UserBlockTests.cs`

**Interfaces:**
- Produces: `UserBlock.Create(Guid blockerId, Guid blockedUserId) -> UserBlock` (throws `DomainException` on self-block); read-only props `Id, BlockerId, BlockedUserId, CreatedAtUtc`.

- [ ] **Step 1: Write the failing test**

Create `tests/Cinora.Domain.Tests/Entities/UserBlockTests.cs`:

```csharp
using Cinora.Domain.Entities;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Tests.Entities;

public class UserBlockTests
{
    [Fact]
    public void Create_to_self_throws_domain_exception()
    {
        var userId = Guid.NewGuid();

        Assert.Throws<DomainException>(() => UserBlock.Create(userId, userId));
    }

    [Fact]
    public void Create_sets_the_directed_pair_and_a_timestamp()
    {
        var blocker = Guid.NewGuid();
        var blocked = Guid.NewGuid();

        var block = UserBlock.Create(blocker, blocked);

        Assert.NotEqual(Guid.Empty, block.Id);
        Assert.Equal(blocker, block.BlockerId);
        Assert.Equal(blocked, block.BlockedUserId);
        Assert.NotEqual(default, block.CreatedAtUtc);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Cinora.Domain.Tests/Cinora.Domain.Tests.csproj --filter "FullyQualifiedName~UserBlockTests"`
Expected: FAIL — `UserBlock` does not exist.

- [ ] **Step 3: Create the entity**

Create `src/Cinora.Domain/Entities/UserBlock.cs`:

```csharp
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Entities;

/// <summary>
/// A directed block: <see cref="BlockerId"/> has blocked <see cref="BlockedUserId"/>. Existence of the row is
/// the block (there is no status/lifecycle); unblocking deletes the row. A block is independent of the
/// <see cref="Friend"/> graph — you can block someone you were never friends with — and it enforces the full
/// cut-off + hide semantics of the Friends &amp; Social overhaul (§2).
/// </summary>
public sealed class UserBlock
{
    private UserBlock()
    {
    }

    /// <summary>The unique identifier of this block record.</summary>
    public Guid Id { get; private set; }

    /// <summary>The user who created the block.</summary>
    public Guid BlockerId { get; private set; }

    /// <summary>The user who is blocked.</summary>
    public Guid BlockedUserId { get; private set; }

    /// <summary>The UTC instant the block was created.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Creates a new block from <paramref name="blockerId"/> against <paramref name="blockedUserId"/>.</summary>
    /// <param name="blockerId">The user creating the block.</param>
    /// <param name="blockedUserId">The user being blocked; must differ from the blocker.</param>
    /// <returns>A new <see cref="UserBlock"/>.</returns>
    /// <exception cref="DomainException">Thrown when a user attempts to block themselves.</exception>
    public static UserBlock Create(Guid blockerId, Guid blockedUserId)
    {
        if (blockerId == blockedUserId)
        {
            throw new DomainException("A user cannot block themselves.");
        }

        return new UserBlock
        {
            Id = Guid.NewGuid(),
            BlockerId = blockerId,
            BlockedUserId = blockedUserId,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Cinora.Domain.Tests/Cinora.Domain.Tests.csproj --filter "FullyQualifiedName~UserBlockTests"`
Expected: PASS.

- [ ] **Step 5: Checkpoint** — build + `dotnet test tests/Cinora.Domain.Tests/...`; hand off (no git).

### Task B1.2: Persistence — DbSet, EF config, migration

**Files:**
- Modify: `src/Cinora.Application/Common/Interfaces/IAppDbContext.cs` (add `DbSet<UserBlock> UserBlocks`)
- Modify: `src/Cinora.Infrastructure/Persistence/CinoraDbContext.cs` (implement `UserBlocks`)
- Create: `src/Cinora.Infrastructure/Persistence/Configurations/UserBlockConfiguration.cs`
- Create (generated): `src/Cinora.Infrastructure/Migrations/<timestamp>_AddUserBlock.cs`
- Test: `tests/Cinora.Web.IntegrationTests/Friends/UserBlockPersistenceTests.cs`

**Interfaces:**
- Consumes: `UserBlock` (Task B1.1), `IAppDbContext`.
- Produces: `IAppDbContext.UserBlocks` (`DbSet<UserBlock>`); a `UserBlocks` table with unique index `(BlockerId, BlockedUserId)` and index `(BlockedUserId)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Cinora.Web.IntegrationTests/Friends/UserBlockPersistenceTests.cs`:

```csharp
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserBlockPersistenceTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserBlockPersistenceTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UserBlock_round_trips_and_duplicate_pair_violates_the_unique_index()
    {
        var blocker = Guid.NewGuid();
        var blocked = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            db.UserBlocks.Add(UserBlock.Create(blocker, blocked));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            Assert.True(await db.UserBlocks.AsNoTracking()
                .AnyAsync(b => b.BlockerId == blocker && b.BlockedUserId == blocked));

            db.UserBlocks.Add(UserBlock.Create(blocker, blocked)); // same directed pair again
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserBlockPersistenceTests"`
Expected: FAIL — `IAppDbContext`/`CinoraDbContext` has no `UserBlocks`.

- [ ] **Step 3: Add the DbSet to the port**

In `src/Cinora.Application/Common/Interfaces/IAppDbContext.cs`, add after the `Friends` property (line 19):

```csharp
    /// <summary>The directed user-block graph (existence = blocked; §2).</summary>
    DbSet<UserBlock> UserBlocks { get; }
```

- [ ] **Step 4: Implement it on the context**

In `src/Cinora.Infrastructure/Persistence/CinoraDbContext.cs`, add after the `Friends` property (line 34):

```csharp
    /// <summary>The directed user-block graph (existence = blocked; §2).</summary>
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
```

- [ ] **Step 5: Add the EF configuration**

Create `src/Cinora.Infrastructure/Persistence/Configurations/UserBlockConfiguration.cs`:

```csharp
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the directed <see cref="UserBlock"/> relationship between two users (§2, §11).</summary>
internal sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.Property(b => b.CreatedAtUtc).IsRequired();

        // WHY: at most one block per directed (blocker, blocked) pair; also the primary lookup "do I block X".
        builder.HasIndex(b => new { b.BlockerId, b.BlockedUserId }).IsUnique();

        // WHY: the reverse lookup "does X block me" and the bulk exclusion set (BlockedOrBlockedByIds).
        builder.HasIndex(b => b.BlockedUserId);

        // Both self-references to Users MUST be Restrict — two cascading FKs into one table is the SQL Server
        // "multiple cascade paths" failure (identical to FriendConfiguration).
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(b => b.BlockerId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(b => b.BlockedUserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 6: Generate the migration**

Run:
```
dotnet ef migrations add AddUserBlock --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
```
Expected: a new `Migrations/<timestamp>_AddUserBlock.cs` creating table `UserBlocks` with the unique `(BlockerId, BlockedUserId)` index and the `(BlockedUserId)` index. Open it and confirm it is **additive only** (one `CreateTable`, no alters/drops of existing tables).

Then apply it to the test DB and regenerate the idempotent script:
```
dotnet ef database update --project src/Cinora.Infrastructure --startup-project src/Cinora.Web
dotnet ef migrations script --idempotent --project src/Cinora.Infrastructure --startup-project src/Cinora.Web -o artifacts/migrate.sql
```
(If `localhost` SQL Server is unreachable, the integration test DB `CinoraTest` is created/migrated automatically by `EnsureDatabaseReadyAsync`; the remote `db58983` apply happens at deployment.)

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserBlockPersistenceTests"`
Expected: PASS.

- [ ] **Step 8: Checkpoint** — build + `dotnet test tests/Cinora.Web.IntegrationTests/...`; hand off (no git).

### Task B1.3: Block/unblock/list verticals + the central block guard

**Files:**
- Create: `src/Cinora.Application/Features/Blocks/BlockQueries.cs`
- Create: `src/Cinora.Application/Features/Blocks/BlockUserCommand.cs`
- Create: `src/Cinora.Application/Features/Blocks/UnblockUserCommand.cs`
- Create: `src/Cinora.Application/Features/Blocks/GetBlockedUsersQuery.cs`
- Test: `tests/Cinora.Web.IntegrationTests/Friends/BlockingTests.cs`

**Interfaces:**
- Consumes: `IAppDbContext`, `ICurrentUser.GetRequiredUserId()`, `UserBlock`, `NotFoundException`, `Friend`.
- Produces:
  - `BlockQueries.AreBlockedEitherWayAsync(IAppDbContext, Guid a, Guid b, CancellationToken) -> Task<bool>`
  - `BlockQueries.BlockedOrBlockedByIdsAsync(IAppDbContext, Guid me, CancellationToken) -> Task<HashSet<Guid>>`
  - `BlockUserCommand(Guid TargetUserId) : IRequest<Unit>`
  - `UnblockUserCommand(Guid TargetUserId) : IRequest<Unit>`
  - `GetBlockedUsersQuery() : IRequest<BlockedUsersVm>`; `BlockedUsersVm(IReadOnlyList<BlockedUserVm> Blocked)`; `BlockedUserVm(Guid UserId, string DisplayName, string? AvatarFileKey)`

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Cinora.Web.IntegrationTests/Friends/BlockingTests.cs`. These drive the commands through `ISender` via a scope (no HTTP yet — the controller wiring is Task B1.5):

```csharp
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class BlockingTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public BlockingTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Block_removes_existing_friendship_and_is_silent_and_idempotent()
    {
        var (me, other) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedUsersAsync(("Me B1", me), ("Other B1", other));
        await SeedAcceptedFriendshipAsync(me, other);

        await SendAsAsync(me, new BlockUserCommand(other));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.True(await db.UserBlocks.AnyAsync(b => b.BlockerId == me && b.BlockedUserId == other));
        Assert.False(await db.Friends.AnyAsync(f =>
            (f.RequesterId == me && f.AddresseeId == other) || (f.RequesterId == other && f.AddresseeId == me)));
        Assert.False(await db.Notifications.AnyAsync(n => n.RecipientUserId == other)); // silent

        // Idempotent: a second block is a no-op, not a duplicate/throw.
        await SendAsAsync(me, new BlockUserCommand(other));
        Assert.Equal(1, await db.UserBlocks.CountAsync(b => b.BlockerId == me && b.BlockedUserId == other));
    }

    [Fact]
    public async Task Block_self_throws_domain_exception()
    {
        var me = Guid.NewGuid();
        await SeedUsersAsync(("Solo B1", me));

        await Assert.ThrowsAsync<DomainException>(() => SendAsAsync(me, new BlockUserCommand(me)));
    }

    [Fact]
    public async Task Unblock_removes_the_block_and_is_idempotent()
    {
        var (me, other) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedUsersAsync(("Me B1b", me), ("Other B1b", other));
        await SendAsAsync(me, new BlockUserCommand(other));

        await SendAsAsync(me, new UnblockUserCommand(other));
        await SendAsAsync(me, new UnblockUserCommand(other)); // idempotent no-op

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.False(await db.UserBlocks.AnyAsync(b => b.BlockerId == me && b.BlockedUserId == other));
    }

    [Fact]
    public async Task Blocked_users_query_returns_only_who_i_blocked()
    {
        var (me, other) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedUsersAsync(("Me B1c", me), ("Blocked Person", other));
        await SendAsAsync(me, new BlockUserCommand(other));

        var vm = await SendAsAsync(me, new GetBlockedUsersQuery());
        Assert.Single(vm.Blocked);
        Assert.Equal(other, vm.Blocked[0].UserId);
        Assert.Equal("Blocked Person", vm.Blocked[0].DisplayName);
    }

    // ---- helpers ----
    private async Task<TResult> SendAsAsync<TResult>(Guid userId, IRequest<TResult> request)
    {
        using var scope = _factory.Services.CreateScope();
        TestCurrentUser.Set(scope, userId); // see note in Step 2
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(request, CancellationToken.None);
    }

    private async Task SeedUsersAsync(params (string Name, Guid Id)[] users)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        foreach (var (name, id) in users)
        {
            db.Set<User>().Add(User.Create(id, name));
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedAcceptedFriendshipAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var friend = Friend.Request(a, b);
        friend.Accept();
        db.Friends.Add(friend);
        await db.SaveChangesAsync();
    }
}
```

> **NOTE for the implementer (Step 2 prerequisite):** driving commands as an arbitrary user requires overriding `ICurrentUser` in the test scope. Check whether the harness already has a helper (grep `tests/Cinora.Web.IntegrationTests/Infrastructure` for `ICurrentUser`/`CurrentUser`/`TestCurrentUser`). If one exists, use it (adjust `TestCurrentUser.Set` above to the real API). If NOT, the simpler path is to drive these four behaviors **through HTTP in Task B1.5** with real signed-in users (like `FriendProfileTests` does) and keep only the *pure* handler-logic checks here using a hand-built handler with an NSubstitute `ICurrentUser` + the real `CinoraDbContext` from a scope. Prefer the existing pattern in the codebase; do not invent a new auth-faking mechanism. Also confirm `User.Create(Guid, string)` is the real factory signature (grep `src/Cinora.Domain/Entities/User.cs`) — if it differs, seed users via `TestAuthentication.RegisterAndSignInAsync` instead and read back their ids.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~BlockingTests"`
Expected: FAIL — the `Blocks` feature types don't exist.

- [ ] **Step 3: Create the block guard**

Create `src/Cinora.Application/Features/Blocks/BlockQueries.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// The single, fail-closed seam for block enforcement (§3). Every surface that must respect a block — user
/// search, profile read, friend-request send, the feed, and chat — routes through here so the rule cannot be
/// applied inconsistently. All checks are cheap seeks on the indexed <c>(BlockerId, BlockedUserId)</c> /
/// <c>(BlockedUserId)</c> columns.
/// </summary>
public static class BlockQueries
{
    /// <summary>True when a block exists in EITHER direction between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static Task<bool> AreBlockedEitherWayAsync(
        IAppDbContext db, Guid a, Guid b, CancellationToken cancellationToken) =>
        db.UserBlocks.AsNoTracking().AnyAsync(
            block => (block.BlockerId == a && block.BlockedUserId == b)
                || (block.BlockerId == b && block.BlockedUserId == a),
            cancellationToken);

    /// <summary>
    /// The set of every user in a block relationship with <paramref name="me"/> (either direction), for bulk
    /// exclusion in list reads (search/feed) with no N+1.
    /// </summary>
    public static async Task<HashSet<Guid>> BlockedOrBlockedByIdsAsync(
        IAppDbContext db, Guid me, CancellationToken cancellationToken)
    {
        var ids = await db.UserBlocks.AsNoTracking()
            .Where(block => block.BlockerId == me || block.BlockedUserId == me)
            .Select(block => block.BlockerId == me ? block.BlockedUserId : block.BlockerId)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }
}
```

- [ ] **Step 4: Create `BlockUserCommand`**

Create `src/Cinora.Application/Features/Blocks/BlockUserCommand.cs`:

```csharp
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// Blocks <paramref name="TargetUserId"/> as the current user (§2, §5). The actor is server-resolved (ADR 0009).
/// In one unit of work it creates the <see cref="UserBlock"/> and removes EVERY <see cref="Friend"/> row for the
/// unordered pair (any status, either direction), fully cutting the two apart. Idempotent — an existing block is
/// a no-op. SILENT — no notification is created (§9). A self-block throws <see cref="Cinora.Domain.Exceptions.DomainException"/> (→ 400).
/// </summary>
/// <param name="TargetUserId">The id of the user to block (the resource).</param>
public sealed record BlockUserCommand(Guid TargetUserId) : IRequest<Unit>;

/// <summary>Handles <see cref="BlockUserCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class BlockUserCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<BlockUserCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var target = request.TargetUserId;

        var targetExists = await db.Users.AsNoTracking().AnyAsync(user => user.Id == target, cancellationToken);
        if (!targetExists)
        {
            throw new NotFoundException($"User ({target}) was not found.");
        }

        var alreadyBlocked = await db.UserBlocks
            .AnyAsync(block => block.BlockerId == me && block.BlockedUserId == target, cancellationToken);
        if (alreadyBlocked)
        {
            return Unit.Value; // idempotent no-op
        }

        // UserBlock.Create throws DomainException (→ 400) on a self-block, before anything is staged.
        db.UserBlocks.Add(UserBlock.Create(me, target));

        // Full cut-off: remove any friendship or pending request between the two (either direction).
        var pairRows = await db.Friends
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == target)
                || (friend.RequesterId == target && friend.AddresseeId == me))
            .ToListAsync(cancellationToken);
        db.Friends.RemoveRange(pairRows);

        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
```

- [ ] **Step 5: Create `UnblockUserCommand`**

Create `src/Cinora.Application/Features/Blocks/UnblockUserCommand.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>
/// Removes the current user's block against <paramref name="TargetUserId"/> (§5). Idempotent — a no-op when no
/// such block exists. Does NOT restore any prior friendship (the other party must send a fresh request).
/// </summary>
/// <param name="TargetUserId">The id of the user to unblock (the resource).</param>
public sealed record UnblockUserCommand(Guid TargetUserId) : IRequest<Unit>;

/// <summary>Handles <see cref="UnblockUserCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class UnblockUserCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UnblockUserCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var target = request.TargetUserId;

        var block = await db.UserBlocks.FirstOrDefaultAsync(
            candidate => candidate.BlockerId == me && candidate.BlockedUserId == target, cancellationToken);
        if (block is not null)
        {
            db.UserBlocks.Remove(block);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
```

- [ ] **Step 6: Create `GetBlockedUsersQuery`**

Create `src/Cinora.Application/Features/Blocks/GetBlockedUsersQuery.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Blocks;

/// <summary>Lists the users the current user has blocked (§5), newest-first, for the Blocked tab.</summary>
public sealed record GetBlockedUsersQuery : IRequest<BlockedUsersVm>;

/// <summary>The current user's blocked list.</summary>
/// <param name="Blocked">The blocked users, newest block first.</param>
public sealed record BlockedUsersVm(IReadOnlyList<BlockedUserVm> Blocked);

/// <summary>One blocked user, projected for the Blocked-tab row (name + avatar only).</summary>
/// <param name="UserId">The blocked user's id (the Unblock action targets it).</param>
/// <param name="DisplayName">The blocked user's display name.</param>
/// <param name="AvatarFileKey">The blocked user's avatar key, or <c>null</c> for a monogram.</param>
public sealed record BlockedUserVm(Guid UserId, string DisplayName, string? AvatarFileKey);

/// <summary>Handles <see cref="GetBlockedUsersQuery"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer.</param>
public sealed class GetBlockedUsersQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetBlockedUsersQuery, BlockedUsersVm>
{
    /// <inheritdoc />
    public async Task<BlockedUsersVm> Handle(GetBlockedUsersQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var blocked = await db.UserBlocks
            .AsNoTracking()
            .Where(block => block.BlockerId == me)
            .OrderByDescending(block => block.CreatedAtUtc)
            .Select(block => db.Users
                .Where(user => user.Id == block.BlockedUserId)
                .Select(user => new BlockedUserVm(user.Id, user.DisplayName, user.AvatarFileKey))
                .First())
            .ToListAsync(cancellationToken);

        return new BlockedUsersVm(blocked);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~BlockingTests"`
Expected: PASS (all four). Fix the `TestCurrentUser`/`User.Create` details per the Step-1 NOTE if they differ.

- [ ] **Step 8: Checkpoint** — build + `dotnet test tests/Cinora.Web.IntegrationTests/...`; hand off (no git).

### Task B1.4: Enforce blocking in friend-request send + profile read

**Files:**
- Modify: `src/Cinora.Application/Features/Friends/SendFriendRequestCommand.cs` (add `Blocked` outcome + pre-check)
- Modify: `src/Cinora.Application/Features/Profiles/ProfileVm.cs` (add `ProfileRelationship.BlockedByMe`)
- Modify: `src/Cinora.Application/Features/Profiles/GetProfileQuery.cs` (block resolution)
- Modify: `src/Cinora.Web/Controllers/FriendsController.cs` (`MessageFor` neutral `Blocked` arm)
- Test: `tests/Cinora.Web.IntegrationTests/Friends/BlockEnforcementTests.cs`

**Interfaces:**
- Consumes: `BlockQueries.AreBlockedEitherWayAsync` (Task B1.3).
- Produces: `FriendRequestOutcome.Blocked = 4`; `ProfileRelationship.BlockedByMe = 5`; profile of a user who blocked me → `NotFoundException`; profile of a user I blocked → `ProfileVm` with `Relationship == BlockedByMe`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cinora.Web.IntegrationTests/Friends/BlockEnforcementTests.cs` (HTTP-driven, mirroring `FriendProfileTests` helpers):

```csharp
using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class BlockEnforcementTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public BlockEnforcementTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Blocked_user_gets_404_on_the_blockers_profile_and_blocker_sees_unblock()
    {
        using var blocker = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocker Bea");
        using var blocked = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocked Bud");

        await BlockAsync(blocker.Client, blocked.UserId);

        // The blocked user cannot see the blocker's profile at all (same 404 as a missing user — no leak).
        using var blockedView = await blocked.Client.GetAsync($"/users/{blocker.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, blockedView.StatusCode);

        // The blocker viewing the blocked user's profile sees the "Unblock" affordance, not their content.
        using var blockerView = await blocker.Client.GetAsync($"/users/{blocked.UserId}");
        Assert.Equal(HttpStatusCode.OK, blockerView.StatusCode);
        Assert.Contains("Unblock", await blockerView.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task BlockAsync(HttpClient client, Guid targetUserId)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/friends");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/friends/{targetUserId}/block");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

> This test needs the `POST /friends/{userId}/block` endpoint from Task B1.5. Sequence B1.4 and B1.5 together (write both, then run). If you implement strictly one task at a time, move this test to B1.5 and cover B1.4 here with a handler-level test using an NSubstitute `ICurrentUser` + a scoped `CinoraDbContext`, asserting `GetProfileQuery` throws `NotFoundException` for the blocked viewer and returns `Relationship == BlockedByMe` for the blocker.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~BlockEnforcementTests"`
Expected: FAIL.

- [ ] **Step 3: Add the `Blocked` outcome + pre-check to `SendFriendRequestCommand`**

In `src/Cinora.Application/Features/Friends/SendFriendRequestCommand.cs`:

(a) add the enum value to `FriendRequestOutcome` (after `ReciprocalPending = 3,`):

```csharp
    /// <summary>A block exists in either direction — the request is silently not created (a neutral message
    /// identical to success is shown so the block is never revealed, §14).</summary>
    Blocked = 4,
```

(b) add `using Cinora.Application.Features.Blocks;` to the usings, and insert the pre-check in `Handle`, immediately AFTER the `addresseeExists` 404 check (after line 94), BEFORE loading `pairRows`:

```csharp
        // Block enforcement (§3): a block in either direction silently prevents the request. The caller-facing
        // message is identical to a normal send, so a blocked sender cannot detect the block (§14).
        if (await BlockQueries.AreBlockedEitherWayAsync(db, me, addressee, cancellationToken))
        {
            return new SendFriendRequestResult(FriendRequestOutcome.Blocked);
        }
```

- [ ] **Step 4: Add `BlockedByMe` to `ProfileRelationship`**

In `src/Cinora.Application/Features/Profiles/ProfileVm.cs`, add to the `ProfileRelationship` enum after `None = 4,`:

```csharp

    /// <summary>The viewer has blocked the owner — they see only an Unblock control, no content (§2, §7).</summary>
    BlockedByMe = 5,
```

- [ ] **Step 5: Add block resolution to `GetProfileQuery`**

In `src/Cinora.Application/Features/Profiles/GetProfileQuery.cs`, add `using Cinora.Application.Features.Blocks;`, then in `Handle` insert the following immediately AFTER the `owner` load (after line 51, before `ResolveRelationshipAsync`):

```csharp
        // Block enforcement (§2.2, §7). Only relevant when viewing someone else.
        if (ownerId != me)
        {
            var ownerBlocksMe = await db.UserBlocks.AsNoTracking()
                .AnyAsync(block => block.BlockerId == ownerId && block.BlockedUserId == me, cancellationToken);
            if (ownerBlocksMe)
            {
                // The blocked viewer cannot see the owner at all — same 404 as a non-existent user (no leak).
                throw new NotFoundException($"User ({ownerId}) was not found.");
            }

            var iBlockOwner = await db.UserBlocks.AsNoTracking()
                .AnyAsync(block => block.BlockerId == me && block.BlockedUserId == ownerId, cancellationToken);
            if (iBlockOwner)
            {
                // The blocker sees a minimal "you blocked this user" profile with an Unblock control only.
                return new ProfileVm
                {
                    UserId = ownerId,
                    DisplayName = owner.DisplayName,
                    AvatarFileKey = owner.AvatarFileKey,
                    IsProfilePublic = owner.IsProfilePublic,
                    Relationship = ProfileRelationship.BlockedByMe,
                    IncomingRequestId = null,
                    CanViewReviews = false,
                    CanViewWatchlist = false,
                    CanViewFriends = false,
                    FriendCount = 0,
                    Reviews = [],
                    Friends = [],
                    WatchlistHighlights = [],
                };
            }
        }
```

- [ ] **Step 6: Neutral controller message for `Blocked`**

In `src/Cinora.Web/Controllers/FriendsController.cs`, update the `MessageFor` switch so `Blocked` returns the SAME text as `Created` (never reveal the block). Change the default/add an arm:

```csharp
        FriendRequestOutcome.ReciprocalPending => "They already sent you a request — respond in your requests.",
        FriendRequestOutcome.Blocked => "Friend request sent.",
        _ => "Friend request sent.",
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~BlockEnforcementTests"` (after Task B1.5 wires the endpoint).
Expected: PASS.

- [ ] **Step 8: Checkpoint** — build + `dotnet test`; hand off (no git).

### Task B1.5: Web wiring — Block/Unblock endpoints + profile control + Blocked tab

**Files:**
- Modify: `src/Cinora.Web/Controllers/FriendsController.cs` (Block, Unblock actions)
- Modify: `src/Cinora.Web/Views/Shared/_ProfileFriendControl.cshtml` (BlockedByMe + Block affordance)
- Create: `src/Cinora.Web/Views/Shared/_BlockedUserCard.cshtml`
- (Blocked-tab rendering is completed in Task B3.2 when the hub is redesigned; this task delivers the endpoints + the profile control + the card partial.)

**Interfaces:**
- Consumes: `BlockUserCommand`, `UnblockUserCommand`, `GetProfileQuery` (returns `ProfileVm` with `BlockedByMe`), `BlockedUserVm`.
- Produces: `POST /friends/{userId:guid}/block`, `DELETE /friends/{userId:guid}/block`; a profile control that renders Block/Unblock.

- [ ] **Step 1: Read the current control**

Read `src/Cinora.Web/Views/Shared/_ProfileFriendControl.cshtml` fully to learn its `switch (Model.Relationship)` structure and the existing button/HTMX idioms (it already switches on `ProfileRelationship` and posts to `/friends/...`).

- [ ] **Step 2: Add the controller actions**

In `src/Cinora.Web/Controllers/FriendsController.cs`, add `using Cinora.Application.Features.Blocks;`, then add these actions (after `Remove`, before the private helpers). They reuse the existing `IsProfileFriendControlTarget()` dual-context pattern:

```csharp
    /// <summary>
    /// Blocks a user (<c>POST /friends/{userId}/block</c>) — full cut-off + hide (§2). On the profile page the
    /// control re-renders in its <see cref="ProfileRelationship.BlockedByMe"/> (Unblock) state; on a list the
    /// row is removed (empty 200). Silent and idempotent (handler).
    /// </summary>
    /// <param name="userId">The id of the user to block (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ProfileFriendControl</c> (profile) or an empty <c>200</c> (list).</returns>
    [HttpPost("{userId:guid}/block")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Block(Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new BlockUserCommand(userId), cancellationToken);

        if (IsProfileFriendControlTarget())
        {
            var profile = await sender.Send(new GetProfileQuery(userId), cancellationToken);
            return PartialView("_ProfileFriendControl", profile);
        }

        return Ok();
    }

    /// <summary>
    /// Unblocks a user (<c>DELETE /friends/{userId}/block</c>). On the profile page the control re-renders in its
    /// post-unblock state (now <see cref="ProfileRelationship.None"/> → "Add friend"); on the Blocked tab the row
    /// is removed (empty 200). Idempotent.
    /// </summary>
    /// <param name="userId">The id of the user to unblock (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The re-rendered <c>_ProfileFriendControl</c> (profile) or an empty <c>200</c> (list).</returns>
    [HttpDelete("{userId:guid}/block")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken cancellationToken)
    {
        await sender.Send(new UnblockUserCommand(userId), cancellationToken);

        if (IsProfileFriendControlTarget())
        {
            var profile = await sender.Send(new GetProfileQuery(userId), cancellationToken);
            return PartialView("_ProfileFriendControl", profile);
        }

        return Ok();
    }
```

- [ ] **Step 3: Add the `BlockedByMe` arm + a Block affordance to the profile control**

In `src/Cinora.Web/Views/Shared/_ProfileFriendControl.cshtml`, add a `case ProfileRelationship.BlockedByMe:` to the switch that renders an **Unblock** button, and add a small **Block** button in the `None`, `Friend`, `RequestIncoming`, and `RequestOutgoing` arms (in a kebab/secondary position). Use the same HTMX idiom the existing Remove button uses, targeting `#profile-friend-control`. Example Unblock button (match the file's existing button classes):

```html
@* BlockedByMe: the viewer has blocked this user; only an Unblock control is shown. *@
case ProfileRelationship.BlockedByMe:
    <button type="button"
            class="@buttonClass"
            hx-delete="@Url.Action("Unblock", "Friends", new { userId = Model.UserId })"
            hx-target="#profile-friend-control"
            hx-swap="outerHTML">
        Unblock
    </button>
    break;
```

Example Block button to add inside the other arms (e.g. after the "Add friend" / "Remove" button), as a secondary destructive action:

```html
<button type="button"
        class="@secondaryDangerClass"
        hx-post="@Url.Action("Block", "Friends", new { userId = Model.UserId })"
        hx-target="#profile-friend-control"
        hx-swap="outerHTML"
        data-confirm="Block this user? They won't be able to find you, message you, or send you requests.">
    Block
</button>
```

Reuse the existing CSP-safe styled-confirm mechanism (grep the codebase for `data-confirm` — Phase 6.5 added a styled confirm; do NOT use native `confirm()`). If `buttonClass`/`secondaryDangerClass` locals don't exist in the file, use the same Tailwind class strings the file already uses for its primary/secondary buttons.

- [ ] **Step 4: Create the Blocked-user card partial**

Create `src/Cinora.Web/Views/Shared/_BlockedUserCard.cshtml`:

```html
@using Cinora.Application.Features.Blocks
@model BlockedUserVm
@{
    // One row on the Blocked tab: avatar + name + Unblock. DisplayName is user content → Razor-encoded (@).
}
<div class="flex items-center justify-between gap-3 rounded-card border border-white/5 bg-surface-2 p-3"
     id="blocked-@Model.UserId">
    <div class="flex items-center gap-3">
        <partial name="_Avatar" model="@(new Cinora.Web.ViewModels.AvatarViewModel(Model.DisplayName, Model.AvatarFileKey))" />
        <span class="text-sm font-medium text-ink">@Model.DisplayName</span>
    </div>
    <button type="button"
            class="rounded-btn border border-white/10 px-3 py-1.5 text-sm font-medium text-ink-muted hover:bg-white/5 hover:text-ink"
            hx-delete="@Url.Action("Unblock", "Friends", new { userId = Model.UserId })"
            hx-target="#blocked-@Model.UserId"
            hx-swap="outerHTML">
        Unblock
    </button>
</div>
```

> Confirm the `_Avatar` partial's model type + constructor by grepping `src/Cinora.Web/Views/Shared/_Avatar.cshtml` and `AvatarViewModel` — adjust the `new AvatarViewModel(...)` call to the real signature.

- [ ] **Step 5: Run the enforcement tests (Task B1.4 Step 1) to verify they pass**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~BlockEnforcementTests"`
Expected: PASS.

- [ ] **Step 6: Checkpoint** — build + full `dotnet test tests/Cinora.Web.IntegrationTests/...` + `npm run build --prefix src/Cinora.Web`; then `/review-security` + `/review-code` + `/review-ui`. Hand off (no git).

### Task B1.6: ADR 0022

**Files:** Create `docs/adr/0022-user-blocking-model.md`.

- [ ] **Step 1:** Write the ADR (match the format of `docs/adr/0009-*.md`): Context (Friend has no block; blocking is separate/directional), Decision (separate `UserBlock` entity, full cut-off + hide, silent, central `BlockQueries` guard), Consequences (extra table + index; mutual-hide + one-sided-control; enforced fail-closed in search/profile/friend-request/feed/chat).
- [ ] **Step 2: Checkpoint** — this is docs-only; no build needed. Hand off (no git).

---

## Milestone B2 — Exact-match user search (`IUserDirectory`)

### Task B2.1: `IUserDirectory` port + adapter + registration

**Files:**
- Create: `src/Cinora.Application/Common/Interfaces/IUserDirectory.cs`
- Create: `src/Cinora.Infrastructure/Identity/UserDirectory.cs`
- Modify: `src/Cinora.Infrastructure/DependencyInjection.cs` (register, after line 154)
- Test: `tests/Cinora.Infrastructure.Tests/Identity/UserDirectoryTests.cs` OR an integration test (see Step 1)

**Interfaces:**
- Produces: `IUserDirectory.FindUserIdByEmailAsync(string email, CancellationToken) -> Task<Guid?>` (exact `NormalizedEmail`, null when none).

- [ ] **Step 1: Write the failing test (integration — real Identity)**

Because `UserDirectory` depends on `UserManager<ApplicationUser>`, an integration test with a real registered user is the reliable proof. Create `tests/Cinora.Web.IntegrationTests/Friends/UserDirectoryTests.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserDirectoryTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserDirectoryTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Resolves_a_user_id_from_an_exact_email_case_insensitively_and_null_otherwise()
    {
        using var user = await TestAuthentication.RegisterAndSignInAsync(_factory, "Dir User");

        using var scope = _factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        Assert.Equal(user.UserId, await directory.FindUserIdByEmailAsync(user.Email.ToUpperInvariant(), default));
        Assert.Null(await directory.FindUserIdByEmailAsync("nobody@example.test", default));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserDirectoryTests"`
Expected: FAIL — `IUserDirectory` not registered/defined.

- [ ] **Step 3: Create the port**

Create `src/Cinora.Application/Common/Interfaces/IUserDirectory.cs`:

```csharp
namespace Cinora.Application.Common.Interfaces;

/// <summary>
/// Resolves a user by an authentication attribute that lives ONLY on the Identity principal (ADR 0003) — today,
/// the email. It keeps the Application layer's exact-match user search (§4) free of any Identity type. Exact
/// match only; returns <c>null</c> when no user has that email.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Returns the id of the user whose email matches <paramref name="email"/> exactly (case-insensitive),
    /// or <c>null</c> when none.</summary>
    /// <param name="email">The full email address to look up.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create the adapter**

Create `src/Cinora.Infrastructure/Identity/UserDirectory.cs`:

```csharp
using Cinora.Application.Common.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Cinora.Infrastructure.Identity;

/// <summary>
/// Identity-backed <see cref="IUserDirectory"/>: exact email lookup via <see cref="UserManager{TUser}"/>, which
/// normalizes the input and seeks the indexed <c>NormalizedEmail</c>. Returns the shared <see cref="Guid"/> key
/// that also identifies the domain <c>User</c> (ADR 0003).
/// </summary>
/// <param name="userManager">The Identity user manager.</param>
public sealed class UserDirectory(UserManager<ApplicationUser> userManager) : IUserDirectory
{
    /// <inheritdoc />
    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var user = await userManager.FindByEmailAsync(email);
        return user?.Id;
    }
}
```

- [ ] **Step 5: Register the adapter**

In `src/Cinora.Infrastructure/DependencyInjection.cs`, immediately after line 154 (`services.AddScoped<ICurrentUser, CurrentUser>();`), add:

```csharp
        services.AddScoped<IUserDirectory, UserDirectory>();
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserDirectoryTests"`
Expected: PASS.

- [ ] **Step 7: Checkpoint** — build + `dotnet test`; hand off (no git).

### Task B2.2: `SearchUsersQuery`

**Files:**
- Create: `src/Cinora.Application/Features/Friends/SearchUsersQuery.cs`
- Test: `tests/Cinora.Web.IntegrationTests/Friends/UserSearchTests.cs`

**Interfaces:**
- Consumes: `IAppDbContext`, `ICurrentUser`, `IUserDirectory`, `BlockQueries.BlockedOrBlockedByIdsAsync`, `ProfileRelationship`, `User.Normalize` (confirm the normalization helper — grep `src/Cinora.Domain/Entities/User.cs` for `Normalize`/`NormalizedDisplayName`).
- Produces: `SearchUsersQuery(string Term) : IRequest<UserSearchVm>`; `UserSearchVm(UserSearchResultVm? Match)`; `UserSearchResultVm(Guid UserId, string DisplayName, string? AvatarFileKey, ProfileRelationship Relationship)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Cinora.Web.IntegrationTests/Friends/UserSearchTests.cs` (HTTP-driven via `GET /friends/search`, which arrives in Task B2.3; if implementing strictly one task at a time, cover the handler here with a scoped `ISender` + NSubstitute `ICurrentUser` per the B1.3 NOTE, then re-run through HTTP in B2.3):

```csharp
using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserSearchTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserSearchTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Exact_username_and_exact_email_match_but_a_partial_term_does_not()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Search Seeker");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Findable Fiona");

        Assert.Contains("Findable Fiona", await SearchAsync(me.Client, "Findable Fiona"), StringComparison.Ordinal);
        Assert.Contains("Findable Fiona", await SearchAsync(me.Client, target.Email), StringComparison.Ordinal);

        // A partial term returns the empty state (exact-match only).
        var partial = await SearchAsync(me.Client, "Findable");
        Assert.DoesNotContain("Findable Fiona", partial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_user_is_absent_from_search_results()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocker Search");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Hidden Harry");

        var token = await TestAuthentication.AntiforgeryTokenAsync(me.Client, "/friends");
        using var block = new HttpRequestMessage(HttpMethod.Post, $"/friends/{target.UserId}/block");
        block.Headers.Add("RequestVerificationToken", token);
        block.Headers.Add("HX-Request", "true");
        using var blockResp = await me.Client.SendAsync(block);
        Assert.Equal(HttpStatusCode.OK, blockResp.StatusCode);

        Assert.DoesNotContain("Hidden Harry", await SearchAsync(me.Client, "Hidden Harry"), StringComparison.Ordinal);
    }

    private static async Task<string> SearchAsync(HttpClient client, string term)
    {
        using var response = await client.GetAsync($"/friends/search?term={Uri.EscapeDataString(term)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserSearchTests"`
Expected: FAIL.

- [ ] **Step 3: Create the query**

Create `src/Cinora.Application/Features/Friends/SearchUsersQuery.cs`. (Confirm the domain normalization: the spec says `User.NormalizedDisplayName` is upper-invariant; use `term.Trim().ToUpperInvariant()` to match, OR the real `User.Normalize` if it is `public static`.) Relationship reuses the profile resolver logic inline (block-excluded users never reach here, so `BlockedByMe` cannot occur):

```csharp
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Application.Features.Blocks;
using Cinora.Application.Features.Profiles;
using Cinora.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Finds a single user by an EXACT username or EXACT email (§4). A term containing '@' is treated as an email
/// (resolved via <see cref="IUserDirectory"/>); otherwise it is matched against the unique
/// <c>User.NormalizedDisplayName</c>. The current user and anyone in a block relationship with them (either
/// direction) are excluded, and a no-match returns an empty result — indistinguishable from a blocked user, so
/// a block is never revealed (§14).
/// </summary>
/// <param name="Term">The full username or full email to look up.</param>
public sealed record SearchUsersQuery(string Term) : IRequest<UserSearchVm>;

/// <summary>The result of a <see cref="SearchUsersQuery"/>: the single match, or <c>null</c> when none.</summary>
/// <param name="Match">The matched user projected for a person card, or <c>null</c>.</param>
public sealed record UserSearchVm(UserSearchResultVm? Match);

/// <summary>One person-card result with the viewer-relative relationship control state.</summary>
/// <param name="UserId">The matched user's id.</param>
/// <param name="DisplayName">The matched user's display name (Razor-encoded on render).</param>
/// <param name="AvatarFileKey">The matched user's avatar key, or <c>null</c>.</param>
/// <param name="Relationship">The viewer's relationship (drives Add friend / Requested / Respond / Friends).</param>
public sealed record UserSearchResultVm(Guid UserId, string DisplayName, string? AvatarFileKey, ProfileRelationship Relationship);

/// <summary>Validates <see cref="SearchUsersQuery"/>: a non-blank, length-bounded term.</summary>
public sealed class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    /// <summary>Configures the rules.</summary>
    public SearchUsersQueryValidator() =>
        RuleFor(query => query.Term)
            .NotEmpty().WithMessage("Enter a username or email.")
            .MaximumLength(256).WithMessage("That search term is too long.");
}

/// <summary>Handles <see cref="SearchUsersQuery"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved viewer.</param>
/// <param name="directory">The Identity-backed email resolver (§4.2).</param>
public sealed class SearchUsersQueryHandler(IAppDbContext db, ICurrentUser currentUser, IUserDirectory directory)
    : IRequestHandler<SearchUsersQuery, UserSearchVm>
{
    /// <inheritdoc />
    public async Task<UserSearchVm> Handle(SearchUsersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();
        var term = request.Term.Trim();

        Guid? candidateId = term.Contains('@', StringComparison.Ordinal)
            ? await directory.FindUserIdByEmailAsync(term, cancellationToken)
            : await db.Users.AsNoTracking()
                .Where(user => user.NormalizedDisplayName == term.ToUpperInvariant())
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (candidateId is null || candidateId == me)
        {
            return new UserSearchVm(null); // no match, or yourself
        }

        var excluded = await BlockQueries.BlockedOrBlockedByIdsAsync(db, me, cancellationToken);
        if (excluded.Contains(candidateId.Value))
        {
            return new UserSearchVm(null); // blocked either way — indistinguishable from not-found (§14)
        }

        var owner = await db.Users.AsNoTracking()
            .Where(user => user.Id == candidateId.Value)
            .Select(user => new { user.Id, user.DisplayName, user.AvatarFileKey })
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return new UserSearchVm(null);
        }

        var relationship = await ResolveRelationshipAsync(me, owner.Id, cancellationToken);
        return new UserSearchVm(new UserSearchResultVm(owner.Id, owner.DisplayName, owner.AvatarFileKey, relationship));
    }

    // The viewer→match relationship for the card's control. Block states cannot occur (excluded above).
    private async Task<ProfileRelationship> ResolveRelationshipAsync(Guid me, Guid otherId, CancellationToken cancellationToken)
    {
        var pairRows = await db.Friends.AsNoTracking()
            .Where(friend => (friend.RequesterId == me && friend.AddresseeId == otherId)
                || (friend.RequesterId == otherId && friend.AddresseeId == me))
            .Select(friend => new { friend.RequesterId, friend.Status })
            .ToListAsync(cancellationToken);

        if (pairRows.Exists(row => row.Status == FriendStatus.Accepted))
        {
            return ProfileRelationship.Friend;
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == otherId))
        {
            return ProfileRelationship.RequestIncoming;
        }

        if (pairRows.Exists(row => row.Status == FriendStatus.Pending && row.RequesterId == me))
        {
            return ProfileRelationship.RequestOutgoing;
        }

        return ProfileRelationship.None;
    }
}
```

- [ ] **Step 4: Run to verify it passes** (after Task B2.3 wires the endpoint)

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserSearchTests"`
Expected: PASS.

- [ ] **Step 5: Checkpoint** — build + `dotnet test`; hand off (no git).

### Task B2.3: Search endpoint + result partial

**Files:**
- Modify: `src/Cinora.Web/Controllers/FriendsController.cs` (`Search` action)
- Create: `src/Cinora.Web/Views/Shared/_UserSearchResult.cshtml`

**Interfaces:**
- Consumes: `SearchUsersQuery`, `UserSearchVm`, `UserSearchResultVm`, the existing `_ProfileFriendControl` idioms (or a lighter person-card control).
- Produces: `GET /friends/search?term=` → `_UserSearchResult` partial.

- [ ] **Step 1: Add the action**

In `src/Cinora.Web/Controllers/FriendsController.cs`, add:

```csharp
    /// <summary>
    /// Exact-match people search (<c>GET /friends/search?term=</c>) for the Find-people hub (§4, §6). Returns a
    /// person card for the single match or an empty state; a blocked/no match are identical (§14). Rate-limited
    /// to blunt enumeration.
    /// </summary>
    /// <param name="term">The full username or full email to look up.</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_UserSearchResult</c> partial.</returns>
    [HttpGet("search")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Search([FromQuery] string? term, CancellationToken cancellationToken)
    {
        var result = string.IsNullOrWhiteSpace(term)
            ? new UserSearchVm(null)
            : await sender.Send(new SearchUsersQuery(term), cancellationToken);
        return PartialView("_UserSearchResult", result);
    }
```

- [ ] **Step 2: Create the result partial**

Create `src/Cinora.Web/Views/Shared/_UserSearchResult.cshtml` (renders the match with the right control, or a calm empty state — all user text `@`-encoded):

```html
@using Cinora.Application.Features.Friends
@using Cinora.Application.Features.Profiles
@model UserSearchVm
@{
    var match = Model.Match;
}
@if (match is null)
{
    <p class="px-1 py-3 text-sm text-ink-subtle">No user found. Try their exact username or email.</p>
}
else
{
    <div class="flex items-center justify-between gap-3 rounded-card border border-white/5 bg-surface-2 p-3">
        <a href="@Url.Action("Show", "Profiles", new { id = match.UserId })" class="flex items-center gap-3">
            <partial name="_Avatar" model="@(new Cinora.Web.ViewModels.AvatarViewModel(match.DisplayName, match.AvatarFileKey))" />
            <span class="text-sm font-medium text-ink">@match.DisplayName</span>
        </a>
        @switch (match.Relationship)
        {
            case ProfileRelationship.Friend:
                <span class="text-sm text-ink-subtle">Friends</span>
                break;
            case ProfileRelationship.RequestOutgoing:
                <span class="text-sm text-ink-subtle">Requested</span>
                break;
            case ProfileRelationship.RequestIncoming:
                <span class="text-sm text-ink-subtle">Respond in your requests</span>
                break;
            default:
                <form hx-post="@Url.Action("Send", "Friends")" hx-swap="outerHTML">
                    <input type="hidden" name="AddresseeUserId" value="@match.UserId" />
                    <button type="submit" class="rounded-btn bg-accent px-3 py-1.5 text-sm font-semibold text-black">Add friend</button>
                </form>
                break;
        }
    </div>
}
```

> Confirm the profile route name/args (grep `ProfilesController` — the spec map says `GET /users/{id:guid}`; use `@Url.Action` with the real action/controller or the literal `/users/@match.UserId`). Confirm `_Avatar`/`AvatarViewModel` as in Task B1.5. The `Send` form posts the existing `SendFriendRequestForm.AddresseeUserId` field.

- [ ] **Step 3: Run the B2.2 search tests to verify they pass**

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj --filter "FullyQualifiedName~UserSearchTests"`
Expected: PASS.

- [ ] **Step 4: Checkpoint** — build + `dotnet test` + `npm run build --prefix src/Cinora.Web`; then `/review-security` (enumeration/rate-limit) + `/review-code` + `/review-ui`. Hand off (no git).

---

## Milestone B3 — Social hub redesign + cancel outgoing

### Task B3.1: `CancelFriendRequestCommand`

**Files:**
- Create: `src/Cinora.Application/Features/Friends/CancelFriendRequestCommand.cs`
- Modify: `src/Cinora.Web/Controllers/FriendsController.cs` (`Cancel` action)
- Test: `tests/Cinora.Web.IntegrationTests/Friends/CancelRequestTests.cs`

**Interfaces:**
- Consumes: `IAppDbContext`, `ICurrentUser`, `Friend`, `ForbiddenAccessException`, `FriendStatus`.
- Produces: `CancelFriendRequestCommand(Guid RequestId) : IRequest<Unit>`; `POST /friends/requests/{id:guid}/cancel`.

- [ ] **Step 1: Write the failing test**

Create `tests/Cinora.Web.IntegrationTests/Friends/CancelRequestTests.cs`:

```csharp
using System.Net;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class CancelRequestTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public CancelRequestTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Requester_can_cancel_but_a_non_requester_gets_403()
    {
        using var requester = await TestAuthentication.RegisterAndSignInAsync(_factory, "Cancel Cara");
        using var addressee = await TestAuthentication.RegisterAndSignInAsync(_factory, "Cancel Target");

        var requestId = await SendRequestAndGetIdAsync(requester, addressee.UserId);

        // The addressee cannot cancel the requester's outgoing request.
        using var byAddressee = await PostAsync(addressee.Client, $"/friends/requests/{requestId}/cancel");
        Assert.Equal(HttpStatusCode.Forbidden, byAddressee.StatusCode);

        // The requester cancels — the pending row is gone.
        using var byRequester = await PostAsync(requester.Client, $"/friends/requests/{requestId}/cancel");
        Assert.Equal(HttpStatusCode.OK, byRequester.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        Assert.False(await db.Friends.AnyAsync(f => f.Id == requestId));
    }

    private async Task<Guid> SendRequestAndGetIdAsync(AuthenticatedTestUser requester, Guid addresseeId)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(requester.Client, "/friends");
        var fields = new Dictionary<string, string>
        {
            ["AddresseeUserId"] = addresseeId.ToString(),
            ["__RequestVerificationToken"] = token,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/friends/requests") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("HX-Request", "true");
        using var response = await requester.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Friends.AsNoTracking()
            .Where(f => f.RequesterId == requester.UserId && f.AddresseeId == addresseeId && f.Status == FriendStatus.Pending)
            .Select(f => f.Id).SingleAsync();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url)
    {
        var token = await TestAuthentication.AntiforgeryTokenAsync(client, "/friends");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("HX-Request", "true");
        return await client.SendAsync(request);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `--filter "FullyQualifiedName~CancelRequestTests"`. Expected: FAIL.

- [ ] **Step 3: Create the command**

Create `src/Cinora.Application/Features/Friends/CancelFriendRequestCommand.cs`:

```csharp
using Cinora.Application.Common.Exceptions;
using Cinora.Application.Common.Interfaces;
using Cinora.Application.Common.Messaging;
using Cinora.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cinora.Application.Features.Friends;

/// <summary>
/// Cancels the current user's OUTGOING pending friend request (§5). The actor is server-resolved (ADR 0009); only
/// the requester may cancel their own request (<see cref="ForbiddenAccessException"/> → 403 otherwise). Idempotent
/// — a request that no longer exists is treated as already cancelled (no-op). Only a <see cref="FriendStatus.Pending"/>
/// row is removed.
/// </summary>
/// <param name="RequestId">The id of the pending <c>Friend</c> request to cancel (the resource; from the route).</param>
public sealed record CancelFriendRequestCommand(Guid RequestId) : IRequest<Unit>;

/// <summary>Handles <see cref="CancelFriendRequestCommand"/>.</summary>
/// <param name="db">The persistence context.</param>
/// <param name="currentUser">The server-resolved acting user.</param>
public sealed class CancelFriendRequestCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : IRequestHandler<CancelFriendRequestCommand, Unit>
{
    /// <inheritdoc />
    public async Task<Unit> Handle(CancelFriendRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var me = currentUser.GetRequiredUserId();

        var friend = await db.Friends.FirstOrDefaultAsync(
            candidate => candidate.Id == request.RequestId, cancellationToken);
        if (friend is null)
        {
            return Unit.Value; // idempotent — already gone
        }

        if (friend.RequesterId != me)
        {
            throw new ForbiddenAccessException("Only the requester can cancel this friend request.");
        }

        if (friend.Status == FriendStatus.Pending)
        {
            db.Friends.Remove(friend);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}
```

- [ ] **Step 4: Add the controller action**

In `src/Cinora.Web/Controllers/FriendsController.cs`, add:

```csharp
    /// <summary>
    /// Cancels the current user's outgoing pending request (<c>POST /friends/requests/{id}/cancel</c>). Requester
    /// -only (403 otherwise). Returns a status partial the request row swaps in.
    /// </summary>
    /// <param name="id">The <c>Friend</c> request id (from the route).</param>
    /// <param name="cancellationToken">Bound from <c>HttpContext.RequestAborted</c>.</param>
    /// <returns>The <c>_FriendActionResult</c> partial.</returns>
    [HttpPost("requests/{id:guid}/cancel")]
    [EnableRateLimiting(RateLimitingPolicies.SocialWrite)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new CancelFriendRequestCommand(id), cancellationToken);
        return PartialView("_FriendActionResult", new FriendActionResultVm("Request cancelled."));
    }
```

- [ ] **Step 5: Run to verify it passes** — `--filter "FullyQualifiedName~CancelRequestTests"`. Expected: PASS.
- [ ] **Step 6: Checkpoint** — build + `dotnet test`; hand off (no git).

### Task B3.2: Tabbed `/friends` hub (Find people / Requests / Friends / Blocked)

**Files:**
- Modify: `src/Cinora.Web/ViewModels/Friends/FriendViewModels.cs` (extend `FriendsPageVm` with the blocked list)
- Modify: `src/Cinora.Web/Controllers/FriendsController.cs` (`Index` dispatches `GetBlockedUsersQuery`)
- Modify: `src/Cinora.Web/Views/Friends/Index.cshtml` (four sections + search box + WhatsApp invite + Cancel button on outgoing)
- Test: extend `tests/Cinora.Web.IntegrationTests/Friends/FriendProfileTests.cs` (or a new hub test)

**Interfaces:**
- Consumes: `GetFriendsQuery`, `GetPendingRequestsQuery`, `GetBlockedUsersQuery`, the `_UserSearchResult`, `_InviteFriends`, `_BlockedUserCard`, `_PendingRequest` partials.
- Produces: a `/friends` page rendering all four sections.

- [ ] **Step 1: Read the current view + VM**

Read `src/Cinora.Web/Views/Friends/Index.cshtml` and `src/Cinora.Web/ViewModels/Friends/FriendViewModels.cs` fully.

- [ ] **Step 2: Write the failing test**

Add to `FriendProfileTests` (or a new `FriendsHubTests`):

```csharp
    [Fact]
    public async Task Hub_shows_find_people_search_and_blocked_section()
    {
        using var me = await RegisterAsync("Hub Owner");
        using var blocked = await RegisterAsync("Hub Blocked");

        var token = await TestAuthentication.AntiforgeryTokenAsync(me.Client, "/friends");
        using var block = new HttpRequestMessage(HttpMethod.Post, $"/friends/{blocked.UserId}/block");
        block.Headers.Add("RequestVerificationToken", token);
        block.Headers.Add("HX-Request", "true");
        using var blockResp = await me.Client.SendAsync(block);
        Assert.Equal(System.Net.HttpStatusCode.OK, blockResp.StatusCode);

        using var page = await me.Client.GetAsync("/friends");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/friends/search", html, StringComparison.Ordinal);  // the search box posts here
        Assert.Contains("Hub Blocked", html, StringComparison.Ordinal);       // the Blocked section renders
    }
```

- [ ] **Step 3: Extend the page VM**

In `src/Cinora.Web/ViewModels/Friends/FriendViewModels.cs`, extend `FriendsPageVm` to carry the blocked list. If it is a record like `FriendsPageVm(FriendsVm Friends, PendingRequestsVm Requests)`, change it to:

```csharp
public sealed record FriendsPageVm(
    FriendsVm Friends,
    PendingRequestsVm Requests,
    Cinora.Application.Features.Blocks.BlockedUsersVm Blocked);
```

- [ ] **Step 4: Dispatch the blocked query in `Index`**

In `FriendsController.Index`, add `using Cinora.Application.Features.Blocks;` and:

```csharp
        var friends = await sender.Send(new GetFriendsQuery(), cancellationToken);
        var requests = await sender.Send(new GetPendingRequestsQuery(), cancellationToken);
        var blocked = await sender.Send(new GetBlockedUsersQuery(), cancellationToken);
        return View(new FriendsPageVm(friends, requests, blocked));
```

- [ ] **Step 5: Redesign the view**

Edit `src/Cinora.Web/Views/Friends/Index.cshtml` to render four labelled sections. Add a **Find people** section at the top:

```html
<section aria-labelledby="find-people-heading" class="space-y-3">
    <h2 id="find-people-heading" class="text-lg font-semibold text-ink">Find people</h2>
    <input type="search" name="term" placeholder="Exact username or email"
           class="w-full rounded-btn border border-white/10 bg-surface-2 px-3 py-2 text-sm text-ink"
           hx-get="@Url.Action("Search", "Friends")"
           hx-trigger="input changed delay:400ms, search"
           hx-target="#user-search-result"
           hx-swap="innerHTML"
           name="term" />
    <div id="user-search-result" aria-live="polite"></div>
    <partial name="_InviteFriends" />
</section>
```

Keep the existing Requests + Friends sections, but add a **Cancel** button to each outgoing pending row (in `_PendingRequest.cshtml`, the outgoing branch — replace the static "Request sent" badge with a Cancel button):

```html
<button type="button"
        class="rounded-btn border border-white/10 px-3 py-1.5 text-sm text-ink-muted hover:bg-white/5"
        hx-post="@Url.Action("Cancel", "Friends", new { id = Model.RequestId })"
        hx-target="closest [data-request-row]"
        hx-swap="outerHTML">
    Cancel
</button>
```

Add a **Blocked** section at the bottom:

```html
<section aria-labelledby="blocked-heading" class="space-y-3">
    <h2 id="blocked-heading" class="text-lg font-semibold text-ink">Blocked</h2>
    @if (Model.Blocked.Blocked.Count == 0)
    {
        <p class="text-sm text-ink-subtle">You haven't blocked anyone.</p>
    }
    else
    {
        foreach (var user in Model.Blocked.Blocked)
        {
            <partial name="_BlockedUserCard" model="user" />
        }
    }
</section>
```

> Match the existing view's container/heading classes and Alpine tab mechanism if it uses one. Confirm the `_PendingRequest` model exposes `RequestId` and a `data-request-row` wrapper (add one if absent). Confirm `@using` directives / `_ViewImports` cover `Cinora.Application.Features.Blocks`.

- [ ] **Step 6: Run to verify it passes** — `--filter "FullyQualifiedName~Hub_shows_find_people"`. Expected: PASS.
- [ ] **Step 7: Checkpoint** — build + `dotnet test` + `npm run build --prefix src/Cinora.Web`; then `/review-ui` + `/review-code`. Hand off (no git).

### Task B3.3: Repoint the "find friends" empty-state CTAs

**Files:**
- Modify: `src/Cinora.Web/Views/Home/Index.cshtml` (feed empty-state CTA)
- Modify: `src/Cinora.Web/Views/Friends/Index.cshtml` (empty-friends CTA, if still pointing at Discovery)
- Test: extend an integration test to assert the CTA points at `/friends`.

- [ ] **Step 1: Write the failing assertion**

Add to a Home feed integration test (or create `tests/Cinora.Web.IntegrationTests/Home/FeedEmptyStateTests.cs`) — a signed-in user with no friends GETs `/home` and the HTML links to `/friends`, not `/discover`:

```csharp
    [Fact]
    public async Task No_friends_empty_state_points_at_find_friends()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Lonely Lee");
        using var page = await me.Client.GetAsync("/home");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/friends", html, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it fails** (the current empty state links `/discover`). Expected: FAIL (or already-passing if `/friends` appears elsewhere — in that case tighten the assertion to the empty-state CTA's `href`/text "Find friends").

- [ ] **Step 3: Repoint the CTAs**

In `src/Cinora.Web/Views/Home/Index.cshtml`, find the no-friends empty state (the button labelled "Discover titles" linking to `Discovery/Index`) and change it to link to `/friends` with the label "Find friends". In `src/Cinora.Web/Views/Friends/Index.cshtml`, ensure the empty-friends card points at the Find-people section (an in-page anchor `#find-people-heading` or just the top of `/friends`), not `Discovery/Index`.

- [ ] **Step 4: Run to verify it passes.** Expected: PASS.
- [ ] **Step 5: Checkpoint** — build + `dotnet test` + `npm run build`; `/review-ui`. Hand off (no git).

---

## Milestone B4 — Authenticated-only access (ADR 0023)

### Task B4.1: Remove `[AllowAnonymous]` from content; keep the public allowlist

**Files:**
- Modify: every controller/action in `src/Cinora.Web/Controllers` currently carrying `[AllowAnonymous]` on content (Discovery/Search/Details, public reviews/comments reads, etc.)
- Modify: `src/Cinora.Web/Controllers/DiscoveryController.cs` etc. — prune dead anonymous branches (e.g. "Sign in to review")
- Test: `tests/Cinora.Web.IntegrationTests/Security/AnonymousLockdownTests.cs`

**Interfaces:**
- Produces: anonymous GET of any content route → 302 to `/account/login`; landing + auth + health remain 200.

- [ ] **Step 1: Enumerate the current `[AllowAnonymous]` usages**

Run: `rg -n "AllowAnonymous" src/Cinora.Web`
Record every hit. Classify each against the allowlist (spec §8): **KEEP** on the landing/marketing page, auth pages (login/register/Google challenge+callback/logout), error/offline pages, `/health`, static; **REMOVE** from everything else (Discovery, Search, Details, reviews/comments reads, Tour if it gates content, etc.).

- [ ] **Step 2: Write the failing tests**

Create `tests/Cinora.Web.IntegrationTests/Security/AnonymousLockdownTests.cs`. Use representative content routes discovered in Step 1 (adjust the exact paths to the real routes):

```csharp
using System.Net;
using Cinora.Web.IntegrationTests.Infrastructure;

namespace Cinora.Web.IntegrationTests.Security;

[Collection(WebIntegrationTestGroup.Name)]
public sealed class AnonymousLockdownTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public AnonymousLockdownTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("/discover")]
    [InlineData("/watchlist")]
    [InlineData("/recommendations")]
    public async Task Anonymous_content_route_redirects_to_login(string path)
    {
        using var client = TestAuthentication.CreateClient(_factory); // anonymous, no redirect-follow
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/account/login")]
    public async Task Public_allowlist_route_stays_reachable_anonymously(string path)
    {
        using var client = TestAuthentication.CreateClient(_factory);
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

> Confirm `TestAuthentication.CreateClient` does NOT auto-follow redirects (the existing F6 test relies on this — `AllowAutoRedirect = false`). Confirm the real landing route (e.g. `/` or `/welcome`) and add it to the allowlist theory if it should be public.

- [ ] **Step 3: Run to verify it fails** — `--filter "FullyQualifiedName~AnonymousLockdownTests"`. Expected: the `/discover` etc. cases FAIL (currently 200 anonymously).

- [ ] **Step 4: Remove the attributes + prune dead branches**

For each REMOVE hit from Step 1, delete the `[AllowAnonymous]` attribute (the global fail-closed fallback policy then requires auth). Remove now-dead anonymous UI branches — e.g. the Details page "Sign in to review" prompt and any `[AllowAnonymous]`-only output-cache variant (spec §8). Keep the allowlist attributes.

- [ ] **Step 5: Run to verify it passes** — `--filter "FullyQualifiedName~AnonymousLockdownTests"`. Expected: PASS. Also run the FULL integration suite — some existing tests may assume anonymous content access (e.g. anonymous Details/reviews-list tests from Phase 3); update or delete those, since the product decision is now auth-only.

Run: `dotnet test tests/Cinora.Web.IntegrationTests/Cinora.Web.IntegrationTests.csproj`
Expected: green (after reconciling Phase-3 anonymous-read tests).

- [ ] **Step 6: Checkpoint** — build + full `dotnet test`; `/review-security` (lockdown completeness — grep proves no stray `[AllowAnonymous]` on content) + `/review-code`. Hand off (no git).

### Task B4.2: ADR 0023

**Files:** Create `docs/adr/0023-authenticated-only-access.md`.

- [ ] **Step 1:** Write the ADR (format of `docs/adr/0021-*.md`): Context (Phase 3 made Details/reviews public; user now wants login-only), Decision (remove `[AllowAnonymous]` from content; public allowlist = landing + auth + error/offline + health + static), Consequences (no anonymous attack surface; dead anonymous branches removed; SEO/deep-link sharing of content is gone — accepted).
- [ ] **Step 2: Checkpoint** — docs-only. Hand off (no git).

---

## End-of-plan gate + deployment

After B4:
- `dotnet build Cinora.sln -c Release` clean (TWAE on); `dotnet test Cinora.sln -c Release` green across all four test projects; `npm run build --prefix src/Cinora.Web` clean.
- Run all review gates (`/review-architecture`, `/review-code`, `/review-security`, `/review-performance`, `/review-ui`) — zero Critical/High. Update `PROGRESS.md` and `REVIEW_BACKLOG.md`.
- **Deployment (human-executed unless the user chose mode B):** publish, FTP the output to `site78342.siteasp.net`, and apply `artifacts/migrate.sql` (incl. `AddUserBlock`) to the remote `db58983` — follow `docs/deployment/monsterasp-filezilla-deploy.md` and `docs/deployment/runbook.md`. Verify posters render and login-gating works on the live app.

---

## Self-Review (author's check against the spec)

**Spec coverage:** §2 blocking model → B1.1–B1.2; §3 central guard → B1.3; §4 exact-match search + `IUserDirectory` → B2.1–B2.2; §5 block/unblock/cancel/list verticals → B1.3 + B3.1; §6 redesigned hub + WhatsApp invite + repointed CTAs → B2.3 + B3.2 + B3.3; §7 profile block control + `BlockedByMe` → B1.4 + B1.5; §8 authenticated-only → B4.1; §9 silent blocking → asserted in B1.3; §10 anti-forgery/authZ/CSP/rate-limit → attributes on every new action; §11 migration → B1.2; §14 no-leak empty result → B2.2; Unit A image fix → B0.1. All covered.

**Placeholder scan:** no "TBD"/"handle edge cases"/"similar to". Where an existing file's exact internals are unseen (views/VMs), the step says "read first" and gives the concrete markup/code to add — not a placeholder.

**Type consistency:** `UserBlock.Create(Guid,Guid)`, `IAppDbContext.UserBlocks`, `BlockQueries.AreBlockedEitherWayAsync`/`BlockedOrBlockedByIdsAsync`, `BlockUserCommand`/`UnblockUserCommand`/`GetBlockedUsersQuery`+`BlockedUsersVm`/`BlockedUserVm`, `CancelFriendRequestCommand(Guid RequestId)`, `FriendRequestOutcome.Blocked`, `ProfileRelationship.BlockedByMe`, `IUserDirectory.FindUserIdByEmailAsync`, `SearchUsersQuery(string Term)`+`UserSearchVm(UserSearchResultVm?)`+`UserSearchResultVm(Guid,string,string?,ProfileRelationship)` — used consistently across tasks.

**Known unknowns the implementer MUST confirm (grep before coding):** the test-harness `ICurrentUser` override mechanism (B1.3 NOTE); `User.Create`/`User.Normalize` signatures; `_Avatar`/`AvatarViewModel` shape; `ProfilesController` route name; `_PendingRequest` model + wrapper; the real landing route for the allowlist. Each is flagged inline at its task.
