using Cinora.Domain.Entities;
using Cinora.Domain.Enums;
using Cinora.Domain.Exceptions;

namespace Cinora.Domain.Tests.Entities;

public class FriendTests
{
    [Fact]
    public void Request_to_self_throws_domain_exception()
    {
        var userId = Guid.NewGuid();

        Assert.Throws<DomainException>(() =>
        {
            _ = Friend.Request(userId, userId);
        });
    }

    [Fact]
    public void Request_creates_a_pending_friendship()
    {
        var friend = Friend.Request(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(FriendStatus.Pending, friend.Status);
        Assert.False(friend.RespondedAtUtc.HasValue);
    }

    [Fact]
    public void Accept_moves_pending_to_accepted_and_stamps_response_time()
    {
        var friend = Friend.Request(Guid.NewGuid(), Guid.NewGuid());

        friend.Accept();

        Assert.Equal(FriendStatus.Accepted, friend.Status);
        Assert.True(friend.RespondedAtUtc.HasValue);
    }

    [Fact]
    public void Decline_moves_pending_to_declined_and_stamps_response_time()
    {
        var friend = Friend.Request(Guid.NewGuid(), Guid.NewGuid());

        friend.Decline();

        Assert.Equal(FriendStatus.Declined, friend.Status);
        Assert.True(friend.RespondedAtUtc.HasValue);
    }

    [Fact]
    public void Accept_on_a_declined_friendship_throws_domain_exception()
    {
        var friend = Friend.Request(Guid.NewGuid(), Guid.NewGuid());
        friend.Decline();

        Assert.Throws<DomainException>(() => friend.Accept());
    }

    [Fact]
    public void Accept_on_an_already_accepted_friendship_throws_domain_exception()
    {
        var friend = Friend.Request(Guid.NewGuid(), Guid.NewGuid());
        friend.Accept();

        Assert.Throws<DomainException>(() => friend.Accept());
    }
}
