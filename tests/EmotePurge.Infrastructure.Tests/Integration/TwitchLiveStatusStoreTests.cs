using System.Text.Json;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis, not a mocked IDatabase: what is being verified is the wire format both sides of the
// store agree on, plus the TTL contract — a key that never expired would keep asserting "offline"
// for a worker that died, which is exactly the failure the TTL exists to prevent.
[Collection("Redis")]
public class TwitchLiveStatusStoreTests(RedisFixture fixture)
{
    [Fact]
    public async Task PublishAsync_RoundTripsThroughReadAsync_AndSetsTheTtl()
    {
        var store = new TwitchLiveStatusStore(fixture.Connection, NullLogger<TwitchLiveStatusStore>.Instance);
        var snapshot = new TwitchLiveStatusSnapshot(
            new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc),
            ["handofblood", "sensitron"]);

        await store.PublishAsync(snapshot, TwitchLiveStatusKeys.TimeToLiveFor(TimeSpan.FromSeconds(300)));

        var read = await store.ReadAsync();
        Assert.NotNull(read);
        Assert.Equal(snapshot.GeneratedAtUtc, read.GeneratedAtUtc);
        Assert.Equal(["handofblood", "sensitron"], read.LiveChannelLogins);

        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync(TwitchLiveStatusKeys.LiveChannels);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalSeconds, 1, 600);
    }

    [Fact]
    public async Task PublishAsync_PublishesAnEmptySet_AsAStatementNotAnAbsence()
    {
        // "Nobody is live" is a poll result like any other — it must come back as an empty list,
        // distinguishable from the missing key that means "no statement".
        var store = new TwitchLiveStatusStore(fixture.Connection, NullLogger<TwitchLiveStatusStore>.Instance);

        await store.PublishAsync(
            new TwitchLiveStatusSnapshot(new DateTime(2026, 8, 3, 3, 0, 0, DateTimeKind.Utc), []),
            TimeSpan.FromMinutes(10));

        var read = await store.ReadAsync();
        Assert.NotNull(read);
        Assert.Empty(read.LiveChannelLogins);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenTheKeyIsAbsent()
    {
        // The key expired (worker dead, poll disabled) or was never written. That absence is the
        // "unknown" the API reports, not an error it has to handle.
        await fixture.Connection.GetDatabase().KeyDeleteAsync(TwitchLiveStatusKeys.LiveChannels);

        Assert.Null(await new TwitchLiveStatusStore(fixture.Connection, NullLogger<TwitchLiveStatusStore>.Instance).ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_ToleratesAPayloadFromADifferentWriterVersion()
    {
        // Rolling deploys put an older worker's payload in front of a newer API. Unknown properties
        // are ignored and a missing login array must read as "nobody live", never as null.
        await fixture.Connection.GetDatabase().StringSetAsync(
            TwitchLiveStatusKeys.LiveChannels,
            """{"generatedAtUtc":"2026-08-03T18:00:00Z","somethingNew":42}""");

        var read = await new TwitchLiveStatusStore(fixture.Connection, NullLogger<TwitchLiveStatusStore>.Instance).ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc), read.GeneratedAtUtc);
        Assert.Empty(read.LiveChannelLogins);
    }
}
