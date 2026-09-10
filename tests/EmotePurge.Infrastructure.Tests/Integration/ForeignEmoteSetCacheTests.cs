using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis (redis:7.2-alpine via RedisFixture) for the one failure mode that cannot be reproduced
// through a substituted IDatabase: StringGetAsync succeeding but returning a payload JsonSerializer
// cannot parse. Same corrupt-payload precedent as ModeratedChannelsProviderTests
// .GetModeratedChannelsAsync_TreatsAnUnreadablePayload_AsAMiss. The RedisException/TimeoutException
// half of ForeignEmoteSetCache's class remark lives container-free in
// Unit/ForeignEmoteSetCacheFailureModeTests.cs instead — no real outage needed to prove a caught
// exception type never leaves the method.
[Collection("Redis")]
public class ForeignEmoteSetCacheTests(RedisFixture fixture)
{
    [Fact]
    public async Task TryGetAsync_UnreadablePayload_ReturnsNullInsteadOfThrowing()
    {
        const string channel = "foreign-cache-corrupt-payload";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        await fixture.Connection.GetDatabase().StringSetAsync(Key(channel), "not-json");

        var result = await cache.TryGetAsync(channel);

        Assert.Null(result);
    }

    private static RedisKey Key(string channel) => $"7tvforeign:{channel}";
}
