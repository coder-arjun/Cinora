using System.Net;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Friends;

/// <summary>
/// Task B3.1 tests for cancelling an OUTGOING friend request (<c>POST /friends/requests/{id}/cancel</c>):
/// requester-only (a non-requester gets 403), and a successful cancel removes the pending row. Driven end to end
/// through the real MVC + anti-forgery + Identity pipeline, mirroring <see cref="FriendProfileTests"/>'s helpers.
/// </summary>
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/friends/requests")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Add("HX-Request", "true");
        using var response = await requester.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();
        return await db.Friends.AsNoTracking()
            .Where(f => f.RequesterId == requester.UserId
                && f.AddresseeId == addresseeId
                && f.Status == FriendStatus.Pending)
            .Select(f => f.Id)
            .SingleAsync();
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
