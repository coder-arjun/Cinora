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
