using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: cache, Helix and the token store are all interfaces.
//
// Two of these tests exist because of a specific failure mode rather than a specific feature: a
// transient Twitch failure must never be written to the cache. Caching a "no" that Twitch never
// actually said would lock every moderator of every channel out for the full TTL, and the only
// visible symptom would be moderators losing their permissions for ten minutes with nothing in the
// logs pointing at Twitch.
public class ModeratorCheckServiceTests
{
    [Fact]
    public async Task IsModeratorAsync_ReturnsCachedPositive_WithoutCallingHelix()
    {
        var cache = Substitute.For<IModRoleCache>();
        cache.TryGetIsModeratorAsync("42", "streamer", Arg.Any<CancellationToken>()).Returns(true);
        var helix = Substitute.For<ITwitchHelixClient>();
        var tokens = TokenService("valid-token");
        var service = new ModeratorCheckService(helix, cache, tokens, NullLogger<ModeratorCheckService>.Instance);

        Assert.True(await service.IsModeratorAsync(Principal(), "streamer"));

        await helix.DidNotReceive().GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // A cache hit must not cost a token refresh either — that is why the token lookup sits
        // after the cache check in the service.
        await tokens.DidNotReceive().GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_ReturnsCachedNegative_WithoutCallingHelix()
    {
        var cache = Substitute.For<IModRoleCache>();
        cache.TryGetIsModeratorAsync("42", "streamer", Arg.Any<CancellationToken>()).Returns(false);
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = new ModeratorCheckService(helix, cache, TokenService("valid-token"), NullLogger<ModeratorCheckService>.Instance);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        await helix.DidNotReceive().GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_DeniesWithoutCallingHelix_WhenNoUsableTokenExists()
    {
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = new ModeratorCheckService(helix, cache, TokenService(null), NullLogger<ModeratorCheckService>.Instance);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        await helix.DidNotReceive().GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Not cached: a later refresh or re-login must be able to resolve this live.
        await cache.DidNotReceive().SetIsModeratorAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_DeniesWithoutWritingTheCache_WhenHelixCannotAnswer()
    {
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelLoginsAsync("valid-token", "42", Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>?)null);
        var service = new ModeratorCheckService(helix, cache, TokenService("valid-token"), NullLogger<ModeratorCheckService>.Instance);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        await cache.DidNotReceive().SetIsModeratorAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_ResolvesLiveAndCaches_WhenTheUserModeratesTheChannel()
    {
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelLoginsAsync("valid-token", "42", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "streamer", "otherchannel" });
        var service = new ModeratorCheckService(helix, cache, TokenService("valid-token"), NullLogger<ModeratorCheckService>.Instance);

        Assert.True(await service.IsModeratorAsync(Principal(), "streamer"));

        await cache.Received(1).SetIsModeratorAsync("42", "streamer", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_CachesTheNegative_WhenHelixAnsweredButTheChannelIsMissing()
    {
        // A confirmed "no" is cacheable — unlike the two failure cases above, Twitch actually said so.
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelLoginsAsync("valid-token", "42", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "otherchannel" });
        var service = new ModeratorCheckService(helix, cache, TokenService("valid-token"), NullLogger<ModeratorCheckService>.Instance);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        await cache.Received(1).SetIsModeratorAsync("42", "streamer", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsModeratorAsync_NormalizesTheChannelName_ForBothCacheKeyAndHelixComparison()
    {
        // Helix returns lowercase logins; the caller may pass "HandOfBlood". Without normalization
        // the comparison silently fails and the cache would be keyed twice for one channel.
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelLoginsAsync("valid-token", "42", Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "handofblood" });
        var service = new ModeratorCheckService(helix, cache, TokenService("valid-token"), NullLogger<ModeratorCheckService>.Instance);

        Assert.True(await service.IsModeratorAsync(Principal(), " HandOfBlood "));

        await cache.Received().TryGetIsModeratorAsync("42", "handofblood", Arg.Any<CancellationToken>());
        await cache.Received(1).SetIsModeratorAsync("42", "handofblood", true, Arg.Any<CancellationToken>());
    }

    private static TwitchPrincipalInfo Principal() => new("42", "somemod", AccessToken: null);

    private static ITwitchUserTokenService TokenService(string? accessToken)
    {
        var tokens = Substitute.For<ITwitchUserTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new TwitchUserTokenResult(accessToken, ReauthRequired: accessToken is null));
        return tokens;
    }
}
