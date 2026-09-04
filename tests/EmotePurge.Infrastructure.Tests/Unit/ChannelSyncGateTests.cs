using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The two key spaces added for issue #54. The name gate alone cannot serialise a rename handover:
// the old and the new login are two different keys pointing at the very same Channel row.
public class ChannelSyncGateTests
{
    [Fact]
    public async Task AcquireByNameAsync_SameName_Serialises()
    {
        var gate = new ChannelSyncGate();

        using var first = await gate.AcquireByNameAsync("handofblood");
        var secondAttempt = gate.AcquireByNameAsync("handofblood");

        Assert.False(secondAttempt.IsCompleted);
        first.Dispose();
        (await secondAttempt.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task AcquireByChannelIdAsync_SameRow_SerialisesEvenThoughTheNamesDiffer()
    {
        var gate = new ChannelSyncGate();
        const string channelId = "3f7c1b2e-0000-4000-8000-000000000001";

        // Two callers that got past the entry gate under two different logins — exactly what a
        // rename handover produces.
        using var oldLogin = await gate.AcquireByNameAsync("oldlogin");
        using var newLogin = await gate.AcquireByNameAsync("newlogin");

        using var firstRow = await gate.AcquireByChannelIdAsync(channelId);
        var secondRowAttempt = gate.AcquireByChannelIdAsync(channelId);

        Assert.False(secondRowAttempt.IsCompleted);
        firstRow.Dispose();
        (await secondRowAttempt.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task NameAndChannelIdKeys_DoNotShareASemaphore()
    {
        var gate = new ChannelSyncGate();

        using var byName = await gate.AcquireByNameAsync("handofblood");
        var byId = gate.AcquireByChannelIdAsync("handofblood");

        // Same string, different key space: a channel whose id happened to equal a login must not
        // deadlock the caller that holds both, since SyncChannelAsync holds them at the same time.
        Assert.True(byId.IsCompleted);
        (await byId).Dispose();
    }

    [Fact]
    public async Task Lease_DisposedTwice_ReleasesOnlyOnce()
    {
        var gate = new ChannelSyncGate();

        var lease = await gate.AcquireByChannelIdAsync("channel-1");
        lease.Dispose();
        lease.Dispose();

        using var next = await gate.AcquireByChannelIdAsync("channel-1");
        var blocked = gate.AcquireByChannelIdAsync("channel-1");
        Assert.False(blocked.IsCompleted);
        next.Dispose();
        (await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
