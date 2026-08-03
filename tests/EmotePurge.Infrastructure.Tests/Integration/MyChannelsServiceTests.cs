using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// In Integration/ rather than Unit/: MyChannelsService takes AppDbContext to resolve the tracked
// state of the channels it assembles. Helix, 7TV and the token store are substituted.
//
// The two properties worth pinning are the ones a user notices immediately when they break: the
// caller's own channel is always in the list (Helix's moderated-channels endpoint never returns it),
// and a degraded third party is reported as a degradation rather than as "you have no channels".
[Collection("Postgres")]
public class MyChannelsServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetMyChannelsAsync_AlwaysContainsTheCallersOwnChannel_AsBroadcaster()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetMyChannelsAsync(Principal("mychannels1_self"));

        var own = Assert.Single(result.Channels);
        Assert.Equal("mychannels1_self", own.ChannelName);
        Assert.True(own.IsBroadcaster);
        Assert.False(own.IsModerator);
        Assert.False(own.IsSevenTvEditor);
        // Never joined, so neither tracked nor active — but still listed, because this is the one
        // channel the user can always decide to add.
        Assert.False(own.IsTracked);
        Assert.False(own.IsBotActive);
    }

    [Fact]
    public async Task GetMyChannelsAsync_KeepsTheOwnChannel_EvenWhenHelixAndSevenTvBothFail()
    {
        await using var db = fixture.CreateDbContext();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>?)null);
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((SevenTvEditorGrants?)null);
        var service = CreateService(db, helix: helix, editors: editors);

        var result = await service.GetMyChannelsAsync(Principal("mychannels2_self"));

        Assert.True(result.HelixUnavailable);
        Assert.True(result.SevenTvUnavailable);
        Assert.Equal("mychannels2_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsHelixUnavailableAndReauthRequired_WhenNoUsableTokenExists()
    {
        // The sharper of the two degradations: only a fresh Twitch login can fix this, so the
        // frontend must offer a re-login instead of a generic "Helix is down" note.
        await using var db = fixture.CreateDbContext();
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = CreateService(db, helix: helix, tokens: TokenService(null, reauthRequired: true));

        var result = await service.GetMyChannelsAsync(Principal("mychannels3_self"));

        Assert.True(result.HelixUnavailable);
        Assert.True(result.ReauthRequired);
        await helix.DidNotReceive().GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsNoDegradation_WhenBothSourcesAnswered()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderatedChannels: [], grants: Grants());

        var result = await service.GetMyChannelsAsync(Principal("mychannels4_self"));

        Assert.False(result.HelixUnavailable);
        Assert.False(result.ReauthRequired);
        Assert.False(result.SevenTvUnavailable);
    }

    [Fact]
    public async Task GetMyChannelsAsync_AnnotatesModeratedChannels_AndNormalizesTheirLogins()
    {
        // Helix answers with display-cased logins; the database holds them lowercase. Without the
        // normalization the same channel would appear twice, once per casing.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderatedChannels: ["MyChannels5_Modded"]);

        var result = await service.GetMyChannelsAsync(Principal("mychannels5_self"));

        var modded = Assert.Single(result.Channels, c => c.ChannelName == "mychannels5_modded");
        Assert.True(modded.IsModerator);
        Assert.False(modded.IsBroadcaster);
    }

    [Fact]
    public async Task GetMyChannelsAsync_AddsSevenTvEditorChannels_ThatHaveNoTwitchRelationshipAtAll()
    {
        // A 7TV editor grant is independent of the Twitch role axis, so it can introduce entirely
        // new channel keys rather than only annotating ones Helix already returned.
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderatedChannels: [], grants: Grants(logins: ["mychannels6_edited"]));

        var result = await service.GetMyChannelsAsync(Principal("mychannels6_self"));

        var edited = Assert.Single(result.Channels, c => c.ChannelName == "mychannels6_edited");
        Assert.True(edited.IsSevenTvEditor);
        Assert.False(edited.IsBroadcaster);
        Assert.False(edited.IsModerator);
    }

    [Fact]
    public async Task GetMyChannelsAsync_CombinesFlags_WhenOneChannelCarriesSeveralRoles()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(
            db,
            moderatedChannels: ["mychannels7_both"],
            grants: Grants(logins: ["mychannels7_both"]));

        var result = await service.GetMyChannelsAsync(Principal("mychannels7_self"));

        var both = Assert.Single(result.Channels, c => c.ChannelName == "mychannels7_both");
        Assert.True(both.IsModerator);
        Assert.True(both.IsSevenTvEditor);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsTrackedAndBotActiveState_FromTheDatabase()
    {
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels8_active", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels8_left", IsBotActive = false, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var service = CreateService(db, moderatedChannels: ["mychannels8_active", "mychannels8_left"]);

        var result = await service.GetMyChannelsAsync(Principal("mychannels8_self"));

        var active = Assert.Single(result.Channels, c => c.ChannelName == "mychannels8_active");
        Assert.True(active.IsTracked);
        Assert.True(active.IsBotActive);

        // A channel that was left stays tracked — the row and its whole history survive a leave.
        var left = Assert.Single(result.Channels, c => c.ChannelName == "mychannels8_left");
        Assert.True(left.IsTracked);
        Assert.False(left.IsBotActive);
    }

    [Fact]
    public async Task GetMyChannelsAsync_SortsTheOwnChannelFirst_ThenAlphabetically()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, moderatedChannels: ["mychannels9_c", "mychannels9_a", "mychannels9_b"]);

        var result = await service.GetMyChannelsAsync(Principal("mychannels9_zzz_self"));

        Assert.Equal(
            ["mychannels9_zzz_self", "mychannels9_a", "mychannels9_b", "mychannels9_c"],
            result.Channels.Select(c => c.ChannelName));
    }

    [Fact]
    public async Task GetMyChannelsAsync_NormalizesTheCallersOwnLogin()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetMyChannelsAsync(Principal("MyChannels10_Self"));

        Assert.Equal("mychannels10_self", Assert.Single(result.Channels).ChannelName);
    }

    [Fact]
    public async Task GetMyChannelsAsync_DerivesLiveState_OnlyForPolledChannels()
    {
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels11_live", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels11_off", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        db.Channels.Add(new Channel { ChannelName = "mychannels11_left", IsBotActive = false, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var polledAt = new DateTime(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc);
        var service = CreateService(
            db,
            moderatedChannels: ["mychannels11_live", "mychannels11_off", "mychannels11_left"],
            liveStatus: new TwitchLiveStatusSnapshot(polledAt, ["mychannels11_live"]));

        var result = await service.GetMyChannelsAsync(Principal("mychannels11_self"));

        Assert.Equal(polledAt, result.LivePolledAtUtc);
        Assert.Equal(ChannelLiveStates.Live, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_live").LiveState);
        // Bot-active, therefore polled: absence from the live set is a real "offline".
        Assert.Equal(ChannelLiveStates.Offline, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_off").LiveState);
        // A left channel was never polled — absence proves nothing about it.
        Assert.Equal(ChannelLiveStates.Unknown, Assert.Single(result.Channels, c => c.ChannelName == "mychannels11_left").LiveState);
    }

    [Fact]
    public async Task GetMyChannelsAsync_ReportsLiveStateUnknown_WhenNoSnapshotExists()
    {
        // Worker down, poll disabled, or the key expired: no statement about anyone, including
        // bot-active channels — and no poll timestamp to report.
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "mychannels12_active", IsBotActive = true, TwitchChannelId = NewTwitchId() });
        await db.SaveChangesAsync();
        var service = CreateService(db, moderatedChannels: ["mychannels12_active"]);

        var result = await service.GetMyChannelsAsync(Principal("mychannels12_self"));

        Assert.Null(result.LivePolledAtUtc);
        Assert.All(result.Channels, c => Assert.Equal(ChannelLiveStates.Unknown, c.LiveState));
    }

    private static TwitchPrincipalInfo Principal(string login) => new("42", login, AccessToken: null);

    private static string NewTwitchId() => Guid.NewGuid().ToString("N")[..16];

    private static SevenTvEditorGrants Grants(string[]? logins = null, string[]? twitchIds = null) =>
        new(new HashSet<string>(logins ?? []), new HashSet<string>(twitchIds ?? []));

    private static ITwitchUserTokenService TokenService(string? accessToken, bool reauthRequired = false)
    {
        var tokens = Substitute.For<ITwitchUserTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new TwitchUserTokenResult(accessToken, reauthRequired));
        return tokens;
    }

    private static MyChannelsService CreateService(
        AppDbContext db,
        ITwitchHelixClient? helix = null,
        ISevenTvEditorService? editors = null,
        ITwitchUserTokenService? tokens = null,
        string[]? moderatedChannels = null,
        SevenTvEditorGrants? grants = null,
        TwitchLiveStatusSnapshot? liveStatus = null)
    {
        if (helix is null && moderatedChannels is not null)
        {
            helix = Substitute.For<ITwitchHelixClient>();
            helix.GetModeratedChannelLoginsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new HashSet<string>(moderatedChannels));
        }

        if (editors is null)
        {
            editors = Substitute.For<ISevenTvEditorService>();
            editors.GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(grants ?? Grants());
        }

        // Default is "no snapshot": the live-status axis stays out of every test that is not
        // explicitly about it.
        var liveStatusReader = Substitute.For<ITwitchLiveStatusReader>();
        liveStatusReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(liveStatus);

        return new MyChannelsService(
            db,
            helix ?? Substitute.For<ITwitchHelixClient>(),
            editors,
            tokens ?? TokenService("valid-token"),
            liveStatusReader);
    }
}
