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
