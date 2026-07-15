using System.Net;
using Cinora.Application.Common.Interfaces;
using Cinora.Domain.Entities;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C4 HTTP tests for the <c>/chat</c> surface through the real MVC + auth pipeline: the page renders for a
/// member, a non-member is refused a conversation they don't belong to, and sending returns the rendered bubble
/// with the body Razor-encoded (a <c>&lt;script&gt;</c> body must not round-trip as executable markup). Uses the
/// authenticated HTTP client from <see cref="TestAuthentication"/>; a direct chat is seeded via the handler so a
/// real conversation id exists.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ChatPageTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ChatPageTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Chat_home_renders_for_an_authenticated_user()
    {
        using var session = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Page A");

        using var response = await session.Client.GetAsync("/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Messages", html, StringComparison.Ordinal);
        Assert.Contains("chat-root", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_conversation_you_are_not_a_member_of_is_refused()
    {
        using var a = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Owner D");
        using var b = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Friend D");
        using var outsider = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Outsider D");
        var conversationId = await SeedDirectConversationAsync(a.UserId, b.UserId);

        using var response = await outsider.Client.GetAsync($"/chat/{conversationId}");

        // The membership gate in the handler surfaces as a 403 (ForbiddenAccessException) via the global handler.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sending_a_message_returns_the_bubble_with_the_body_html_encoded()
    {
        using var a = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Sender E");
        using var b = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat Peer E");
        var conversationId = await SeedDirectConversationAsync(a.UserId, b.UserId);

        // The global AutoValidateAntiforgeryToken filter requires the token; the GET also sets the paired cookie.
        var token = await TestAuthentication.AntiforgeryTokenAsync(a.Client, "/chat");

        const string payload = "<script>alert('x')</script>";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Body"] = payload,
            ["__RequestVerificationToken"] = token,
        });
        using var response = await a.Client.PostAsync($"/chat/{conversationId}/messages", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // Razor-encoded: the raw script tag must NOT appear; its encoded form must.
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    private async Task<Guid> SeedDirectConversationAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

        var friendship = Friend.Request(a, b);
        friendship.Accept();
        db.Friends.Add(friendship);
        await db.SaveChangesAsync();

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.GetRequiredUserId().Returns(a);
        return await new Application.Features.Chat.StartDirectConversationCommandHandler(db, currentUser)
            .Handle(new Application.Features.Chat.StartDirectConversationCommand(b), CancellationToken.None);
    }
}
