using Cinora.Domain.Entities;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Tests.Entities;

public class MessageTests
{
    [Fact]
    public void Create_rejects_a_blank_body()
    {
        Assert.Throws<DomainException>(() => Message.Create(Guid.NewGuid(), Guid.NewGuid(), "   "));
    }

    [Fact]
    public void Create_sets_body_and_is_not_deleted()
    {
        var m = Message.Create(Guid.NewGuid(), Guid.NewGuid(), "hello 👋");
        Assert.Equal("hello 👋", m.Body);
        Assert.False(m.IsDeleted);
        Assert.NotEqual(default, m.SentAtUtc);
    }

    [Fact]
    public void MarkDeleted_sets_the_tombstone_flag()
    {
        var m = Message.Create(Guid.NewGuid(), Guid.NewGuid(), "bye");
        m.MarkDeleted();
        Assert.True(m.IsDeleted);
    }
}
