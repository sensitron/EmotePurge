using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against a real redis:7.2-alpine container, because the whole guard is one Redis primitive: SET
// with NX and EX. A mock would assert that we call it, not that it behaves atomically or that the
// key actually expires — and both of those are the point.
[Collection("Redis")]
public class ChannelResyncCooldownTests(RedisFixture fixture)
{
    private static IConfiguration BuildConfiguration(int cooldownSeconds = 60) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SevenTv:ManualResyncCooldownSeconds"] = cooldownSeconds.ToString()
            })
            .Build();

    private static string NewChannel() => $"cooldown{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task TryBeginAsync_AcquiresOnTheFirstCall()
    {
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(), NullLogger<ChannelResyncCooldown>.Instance);

        var state = await cooldown.TryBeginAsync(NewChannel());

        Assert.True(state.Acquired);
    }

    [Fact]
    public async Task TryBeginAsync_BlocksTheSecondCall_AndReportsTheRemainingTime()
    {
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(60), NullLogger<ChannelResyncCooldown>.Instance);

        await cooldown.TryBeginAsync(channel);
        var second = await cooldown.TryBeginAsync(channel);

        Assert.False(second.Acquired);
        // The remaining time, not the configured window: a client uses it to say when to try again.
        Assert.InRange(second.RetryAfterSeconds, 1, 60);
    }

    [Fact]
    public async Task TryBeginAsync_SetsATtl_SoACrashedRequestCannotBlockTheChannelForever()
    {
        // The single worst bug this class could have: SET NX without EX is a permanent lock, and
        // nothing in the codebase would ever delete that key.
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(60), NullLogger<ChannelResyncCooldown>.Instance);

        await cooldown.TryBeginAsync(channel);
        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync($"resync:cooldown:{channel}");

        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task ReleaseAsync_MakesTheSlotImmediatelyAvailableAgain()
    {
        // The path taken when the resync did not happen at all (unknown or inactive channel).
        // Without it, asking about a channel that was just rejoined would block the resync the
        // broadcaster actually wants for a full window.
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(), NullLogger<ChannelResyncCooldown>.Instance);

        await cooldown.TryBeginAsync(channel);
        await cooldown.ReleaseAsync(channel);
        var again = await cooldown.TryBeginAsync(channel);

        Assert.True(again.Acquired);
    }

    [Fact]
    public async Task TryBeginAsync_NormalizesTheChannelName()
    {
        // Regel 9. A cooldown that could be bypassed by typing "HandOfBlood" instead of
        // "handofblood" would be no cooldown at all — and Twitch names are typed with capitals.
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(), NullLogger<ChannelResyncCooldown>.Instance);

        await cooldown.TryBeginAsync(channel.ToUpperInvariant());
        var second = await cooldown.TryBeginAsync($"  {channel}  ");

        Assert.False(second.Acquired);
    }

    [Fact]
    public async Task TryBeginAsync_AcquiresAgain_OnceTheWindowHasPassed()
    {
        // One second so the test does not have to wait a minute; the mechanism is the same.
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(1), NullLogger<ChannelResyncCooldown>.Instance);

        Assert.True((await cooldown.TryBeginAsync(channel)).Acquired);
        Assert.False((await cooldown.TryBeginAsync(channel)).Acquired);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.True((await cooldown.TryBeginAsync(channel)).Acquired);
    }

    [Fact]
    public async Task TryBeginAsync_LetsExactlyOneOfManySimultaneousCallersThrough()
    {
        // The reason this is SET NX and not "read, decide, write": fifteen moderators clicking at
        // once is the realistic case, and a check-then-set would let all fifteen through.
        var channel = NewChannel();
        var cooldown = new ChannelResyncCooldown(fixture.Connection, BuildConfiguration(), NullLogger<ChannelResyncCooldown>.Instance);

        var states = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => cooldown.TryBeginAsync(channel)));

        Assert.Single(states, state => state.Acquired);
    }
}
