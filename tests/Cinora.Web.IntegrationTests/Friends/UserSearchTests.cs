using Cinora.Application.Common.Interfaces;
using Cinora.Application.Features.Friends;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Task B2.2 handler-level tests for <see cref="SearchUsersQueryHandler"/> — exact-match user search (§4). The
/// HTTP endpoint arrives in Task B2.3; here the handler is constructed directly against a scoped
/// <see cref="CinoraDbContext"/> and the real <see cref="IUserDirectory"/>, with an NSubstitute
/// <see cref="ICurrentUser"/> standing in for the searching user. Users are seeded via
/// <see cref="TestAuthentication.RegisterAndSignInAsync"/> so the real domain <c>User</c> (with its
/// <c>NormalizedDisplayName</c>) and Identity email exist. Proves: exact username match, exact email match,
/// partial-term no-match, self exclusion, and blocked-either-way exclusion (indistinguishable from not-found).
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class UserSearchTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public UserSearchTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Exact_username_matches()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Search Seeker");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Findable Fiona");

        var vm = await SearchAsAsync(me.UserId, target.DisplayName);

        Assert.NotNull(vm.Match);
        Assert.Equal(target.UserId, vm.Match!.UserId);
    }

    [Fact]
    public async Task Exact_email_matches()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Search Seeker Two");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Findable Fred");

        var vm = await SearchAsAsync(me.UserId, target.Email);

        Assert.NotNull(vm.Match);
        Assert.Equal(target.UserId, vm.Match!.UserId);
    }

    [Fact]
    public async Task Partial_term_does_not_match()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Search Seeker Three");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Zephyrine Longname");

        // A partial prefix of the display name must NOT match — exact-match only.
        var partial = target.DisplayName[..4];
        var vm = await SearchAsAsync(me.UserId, partial);

        Assert.Null(vm.Match);
    }

    [Fact]
    public async Task Self_is_excluded()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Solo Searcher");

        var vm = await SearchAsAsync(me.UserId, me.DisplayName);

        Assert.Null(vm.Match);
    }

    [Fact]
    public async Task Blocked_either_way_is_excluded()
    {
        using var me = await TestAuthentication.RegisterAndSignInAsync(_factory, "Blocker Searcher");
        using var target = await TestAuthentication.RegisterAndSignInAsync(_factory, "Hidden Harriet");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
            db.UserBlocks.Add(UserBlock.Create(me.UserId, target.UserId));
            await db.SaveChangesAsync();
        }

        var vm = await SearchAsAsync(me.UserId, target.DisplayName);

        Assert.Null(vm.Match);
    }

    // Constructs SearchUsersQueryHandler directly with a scoped CinoraDbContext + the real IUserDirectory + an
    // NSubstitute ICurrentUser returning the searching user's id (no HTTP context / ISender pipeline needed).
    private async Task<UserSearchVm> SearchAsAsync(Guid meUserId, string term)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(meUserId);
        var handler = new SearchUsersQueryHandler(db, currentUser, directory);
        return await handler.Handle(new SearchUsersQuery(term), CancellationToken.None);
    }
}
