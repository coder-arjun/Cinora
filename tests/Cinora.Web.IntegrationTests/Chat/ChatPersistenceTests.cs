using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Infrastructure.Persistence;
using Cinora.Web.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinora.Web.IntegrationTests.Chat;

/// <summary>
/// Task C1.3 persistence tests for the three chat entities (<see cref="Conversation"/>,
/// <see cref="ConversationMember"/>, <see cref="Message"/>) — mirrors
/// <c>UserBlockPersistenceTests</c> exactly for structure. Confirms the round trip through
/// <see cref="CinoraDbContext"/> and that <c>ConversationMemberConfiguration</c>'s unique
/// <c>(ConversationId, UserId)</c> index rejects a duplicate membership row.
/// </summary>
[Collection(WebIntegrationTestGroup.Name)]
public sealed class ChatPersistenceTests : IAsyncLifetime
{
    private readonly CinoraWebApplicationFactory _factory;

    public ChatPersistenceTests(CinoraWebApplicationFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseReadyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Conversation_member_and_message_round_trip_and_duplicate_membership_violates_the_unique_index()
    {
        // Conversation/ConversationMember/Message all carry Restrict FKs to Users (mirroring
        // UserBlockConfiguration/FriendConfiguration) — so, like UserBlockPersistenceTests, the participants
        // must be REAL registered users rather than arbitrary GUIDs.
        using var userA = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat User A1");
        using var userB = await TestAuthentication.RegisterAndSignInAsync(_factory, "Chat User B1");
        var a = userA.UserId;
        var b = userB.UserId;

        Guid conversationId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            var conversation = Conversation.CreateDirect(a, b);
            conversationId = conversation.Id;
            db.Conversations.Add(conversation);
            db.ConversationMembers.Add(ConversationMember.Create(conversationId, a, ConversationRole.Member));
            db.ConversationMembers.Add(ConversationMember.Create(conversationId, b, ConversationRole.Member));
            await db.SaveChangesAsync();

            var message = Message.Create(conversationId, a, "Hello there");
            db.Messages.Add(message);
            conversation.BumpLastMessage(message.SentAtUtc);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CinoraDbContext>();

            Assert.True(await db.Conversations.AsNoTracking()
                .AnyAsync(c => c.Id == conversationId && c.Type == ConversationType.Direct));
            Assert.Equal(2, await db.ConversationMembers.AsNoTracking()
                .CountAsync(m => m.ConversationId == conversationId));
            Assert.True(await db.Messages.AsNoTracking()
                .AnyAsync(m => m.ConversationId == conversationId && m.SenderId == a && m.Body == "Hello there"));

            // Duplicate (ConversationId, UserId) membership pair must violate the unique index.
            db.ConversationMembers.Add(ConversationMember.Create(conversationId, a, ConversationRole.Member));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
