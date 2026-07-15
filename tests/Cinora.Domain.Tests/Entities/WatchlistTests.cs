using Cinora.Domain.Entities;
using Cinora.Domain.Enums;

namespace Cinora.Domain.Tests.Entities;

public class WatchlistTests
{
    [Fact]
    public void Add_creates_an_entry_with_the_initial_status_and_no_update_time()
    {
        var entry = Watchlist.Add(Guid.NewGuid(), Guid.NewGuid(), WatchlistStatus.PlanToWatch);

        Assert.Equal(WatchlistStatus.PlanToWatch, entry.Status);
        Assert.False(entry.UpdatedAtUtc.HasValue);
    }

    [Fact]
    public void ChangeStatus_updates_the_status_and_stamps_updated_time()
    {
        var entry = Watchlist.Add(Guid.NewGuid(), Guid.NewGuid(), WatchlistStatus.PlanToWatch);

        entry.ChangeStatus(WatchlistStatus.Watched);

        Assert.Equal(WatchlistStatus.Watched, entry.Status);
        Assert.True(entry.UpdatedAtUtc.HasValue);
    }
}
