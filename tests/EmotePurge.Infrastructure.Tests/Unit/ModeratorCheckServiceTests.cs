using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: the whole moderated-channel lookup sits behind one interface. Since the shared
// list cache exists, this service neither paginates Helix nor caches anything itself — pagination,
// single-flight and the "never cache a failure" rule all live in ModeratedChannelsProvider and are
// tested there.
//
// What is left to pin here is the membership decision and, above all, that "moderates nothing" and
// "could not be determined" stay two different things. Both deny, so the bool alone cannot tell
// them apart; only the second one is a failure an operator must be able to see. Collapsing them
// would hide a Twitch outage behind a perfectly ordinary-looking "not a moderator".
public class ModeratorCheckServiceTests
{
    [Fact]
    public async Task IsModeratorAsync_ReturnsTrue_WhenTheChannelIsInTheModeratedList()
    {
        var principal = Principal();
        var provider = Provider(Lookup("streamer", "otherchannel"));
        var logger = new RecordingLogger<ModeratorCheckService>();
        var service = new ModeratorCheckService(provider, logger);

        Assert.True(await service.IsModeratorAsync(principal, "streamer"));

        await provider.Received(1).GetModeratedChannelsAsync(principal, Arg.Any<CancellationToken>());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task IsModeratorAsync_ReturnsFalse_WhenTheListAnsweredButOmitsTheChannel()
    {
        var provider = Provider(Lookup("otherchannel"));
        var logger = new RecordingLogger<ModeratorCheckService>();
        var service = new ModeratorCheckService(provider, logger);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        // A confirmed "no" is not an incident and must not be logged as one.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task IsModeratorAsync_ReturnsFalseWithoutLogging_WhenTheUserModeratesNothing()
    {
        // An empty, non-null list is a real answer: Twitch said this user moderates no channel at
        // all. Indistinguishable from the case below by return value — the absent log line is what
        // separates them.
        var provider = Provider(Lookup());
        var logger = new RecordingLogger<ModeratorCheckService>();
        var service = new ModeratorCheckService(provider, logger);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task IsModeratorAsync_DeniesAndLogs_WhenTheListCannotBeDetermined()
    {
        // No usable token, a transient Helix failure or an unfinished pagination. Denied like the
        // empty list above, but reported — and the provider caches nothing in this case, so the
        // next request resolves live again.
        var provider = Provider(new ModeratedChannelsLookup(null, ReauthRequired: true));
        var logger = new RecordingLogger<ModeratorCheckService>();
        var service = new ModeratorCheckService(provider, logger);

        Assert.False(await service.IsModeratorAsync(Principal(), "streamer"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public async Task IsModeratorAsync_NormalizesTheChannelName_BeforeComparingIt()
    {
        // Twitch logins are typed with capitals ("HandOfBlood"), the list holds them normalized.
        // Without normalization the comparison silently fails and a moderator loses access.
        var provider = Provider(Lookup("handofblood"));
        var service = new ModeratorCheckService(provider, new RecordingLogger<ModeratorCheckService>());

        Assert.True(await service.IsModeratorAsync(Principal(), " HandOfBlood "));
    }

    private static TwitchPrincipalInfo Principal() => new("42", "somemod", AccessToken: null);

    private static ModeratedChannelsLookup Lookup(params string[] logins) =>
        new([.. logins.Select((login, index) => new TwitchModeratedChannelInfo(login, $"broadcaster-{index}"))], ReauthRequired: false);

    private static IModeratedChannelsProvider Provider(ModeratedChannelsLookup lookup)
    {
        var provider = Substitute.For<IModeratedChannelsProvider>();
        provider.GetModeratedChannelsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>()).Returns(lookup);
        return provider;
    }
}
