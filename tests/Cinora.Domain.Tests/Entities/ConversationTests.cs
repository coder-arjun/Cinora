using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Tests.Entities;

public class ConversationTests
{
    [Fact]
    public void CreateDirect_to_self_throws()
    {
        var id = Guid.NewGuid();
        Assert.Throws<DomainException>(() => Conversation.CreateDirect(id, id));
    }

    [Fact]
    public void CreateDirect_sets_type_direct_and_no_title()
    {
        var c = Conversation.CreateDirect(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ConversationType.Direct, c.Type);
        Assert.Null(c.Title);
    }

    [Fact]
    public void CreateGroup_requires_a_title_and_is_type_group()
    {
        var c = Conversation.CreateGroup(Guid.NewGuid(), "Movie Buddies");
        Assert.Equal(ConversationType.Group, c.Type);
        Assert.Equal("Movie Buddies", c.Title);
        Assert.Throws<DomainException>(() => Conversation.CreateGroup(Guid.NewGuid(), "  "));
    }

    [Fact]
    public void BumpLastMessage_advances_the_sort_stamp()
    {
        var c = Conversation.CreateGroup(Guid.NewGuid(), "G");
        var when = DateTime.UtcNow.AddMinutes(5);
        c.BumpLastMessage(when);
        Assert.Equal(when, c.LastMessageAtUtc);
    }
}
