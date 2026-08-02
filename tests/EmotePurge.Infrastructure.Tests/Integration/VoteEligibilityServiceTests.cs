using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// In Integration/ rather than Unit/ despite being a pure decision service: VoteEligibilityService
// takes AppDbContext and loads the channel/session pair itself, so the "no container needed"
// assumption from the report does not hold here. Everything except the database is substituted.
//
// The rule this file exists to nail down is the one that lives only in a comment today: EvaluateAsync
// gates casting a vote and therefore rejects an ended session, while EvaluateAudienceAsync gates
// *viewing* the results and deliberately does not. The two methods otherwise share their entire role
// evaluation, so the difference is a single early return that nothing else would catch if it moved.
[Collection("Postgres")]
public class VoteEligibilityServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EvaluateAsync_ReturnsSessionNotFound_ForAnUnknownChannel()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.SessionNotFound, await service.EvaluateAsync(Principal(), "doesnotexist", 1));
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsSessionNotFound_ForAnUnknownSession()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligunknown1");
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.SessionNotFound, await service.EvaluateAsync(Principal(), channel.ChannelName, 999_999));
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsSessionNotFound_ForASessionOfAnotherChannel()
    {
        // Multi-tenant isolation at the eligibility layer: session ids are globally unique, so only
        // the ChannelId predicate keeps channel A's members out of channel B's session.
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "eligforeign1a");
        var channelB = await SeedChannelAsync(db, "eligforeign1b");
        var sessionB = await SeedSessionAsync(db, channelB.Id, AllowedRoles.Everyone);
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.SessionNotFound, await service.EvaluateAsync(Principal(), channelA.ChannelName, sessionB.Id));
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsSessionEnded_ForAnInactiveSession_EvenForAnOtherwiseEligibleVoter()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligended1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Everyone, isActive: false);
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.SessionEnded, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));
    }

    [Fact]
    public async Task EvaluateAudienceAsync_StillAllowsAnInactiveSession()
    {
        // The whole point of the second method: voting is closed, but the audience that was always
        // meant to see this session keeps access to its final results.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligended2");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Everyone, isActive: false);
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAudienceAsync(Principal(), channel.ChannelName, session.Id));
    }

    [Fact]
    public async Task EvaluateAudienceAsync_StillReturnsSessionNotFound_ForASessionOfAnotherChannel()
    {
        // Dropping the IsActive gate must not have dropped the isolation gate with it.
        await using var db = fixture.CreateDbContext();
        var channelA = await SeedChannelAsync(db, "eligforeign2a");
        var channelB = await SeedChannelAsync(db, "eligforeign2b");
        var sessionB = await SeedSessionAsync(db, channelB.Id, AllowedRoles.Everyone);
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.SessionNotFound, await service.EvaluateAudienceAsync(Principal(), channelA.ChannelName, sessionB.Id));
    }

    [Fact]
    public async Task EvaluateAsync_AllowsEveryone_WithoutAskingAnyoneElse()
    {
        // Everyone short-circuits ahead of the management check on purpose — it is the cheapest
        // possible answer and must not cost a Twitch call.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligeveryone1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Everyone);
        var access = Substitute.For<IChannelAccessService>();
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = CreateService(db, access: access, helix: helix);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await access.DidNotReceive().CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AllowedRoles.Mods)]
    [InlineData(AllowedRoles.Broadcaster)]
    [InlineData(AllowedRoles.Subs)]
    [InlineData(AllowedRoles.Subs | AllowedRoles.Mods)]
    public async Task EvaluateAsync_AlwaysAllowsWhoeverManagesTheChannel_WhateverTheSessionTargets(AllowedRoles roles)
    {
        // Admin/broadcaster/mods outrank the AllowedVoterRoles flags entirely — the same precedence
        // join/leave uses. A Subs-only session must not lock out the broadcaster who created it.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"eligmanage{(int)roles}");
        var session = await SeedSessionAsync(db, channel.Id, roles);
        var access = Substitute.For<IChannelAccessService>();
        access.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), channel.ChannelName, Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(db, access: access);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));
    }

    [Theory]
    [InlineData(AllowedRoles.Mods)]
    [InlineData(AllowedRoles.Broadcaster)]
    [InlineData(AllowedRoles.VIPs)]
    [InlineData(AllowedRoles.Mods | AllowedRoles.Broadcaster | AllowedRoles.VIPs)]
    public async Task EvaluateAsync_RejectsAnOrdinaryViewer_ForEveryRoleSetWithoutEveryoneOrSubs(AllowedRoles roles)
    {
        // The flags that are not Everyone and not Subs are decided solely by CanManageChannelAsync —
        // VIPs in particular have no Helix self-check at all, which is the documented limitation.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"eligreject{(int)roles}");
        var session = await SeedSessionAsync(db, channel.Id, roles);
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = CreateService(db, helix: helix);

        Assert.Equal(VoteEligibilityResult.RoleNotEligible, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_SkipsTheSubCheck_WhenTheChannelHasNoTwitchIdYet()
    {
        // Helix needs the broadcaster id; a channel row that has never synced does not have one.
        // The sub check is skipped rather than guessed — denying is the safe direction.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsubnoid1", withTwitchId: false);
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var helix = Substitute.For<ITwitchHelixClient>();
        var cache = Substitute.For<IModRoleCache>();
        var service = CreateService(db, cache: cache, helix: helix);

        Assert.Equal(VoteEligibilityResult.RoleNotEligible, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().TryGetIsSubscriberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_AllowsSubscriber_FromCache_WithoutCallingHelix()
    {
        // The cache is what keeps a session list with ten Subs sessions from firing ten identical
        // Helix calls (10s timeout each) for a single page view.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsubcache1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var cache = Substitute.For<IModRoleCache>();
        cache.TryGetIsSubscriberAsync("42", channel.TwitchChannelId!, Arg.Any<CancellationToken>()).Returns(true);
        var helix = Substitute.For<ITwitchHelixClient>();
        var tokens = TokenService("valid-token");
        var service = CreateService(db, cache: cache, helix: helix, tokens: tokens);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // A cache hit must never cost a token refresh — that is why the token lookup sits behind it.
        await tokens.DidNotReceive().GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_RejectsNonSubscriber_FromCache()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsubcache2");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var cache = Substitute.For<IModRoleCache>();
        cache.TryGetIsSubscriberAsync("42", channel.TwitchChannelId!, Arg.Any<CancellationToken>()).Returns(false);
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = CreateService(db, cache: cache, helix: helix);

        Assert.Equal(VoteEligibilityResult.RoleNotEligible, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_ResolvesSubscriberLiveAndCaches_OnACacheMiss()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsublive1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUserSubscriptionStatusAsync("valid-token", channel.TwitchChannelId!, "42", Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(db, cache: cache, helix: helix);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await cache.Received(1).SetIsSubscriberAsync("42", channel.TwitchChannelId!, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_RejectsWithoutWritingTheCache_WhenHelixCannotAnswer()
    {
        // null is "Helix could not tell us" (rate limit, outage, expired token). Caching that as
        // "not subscribed" would silently drop a subscriber's sessions for the whole TTL.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsublive2");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUserSubscriptionStatusAsync("valid-token", channel.TwitchChannelId!, "42", Arg.Any<CancellationToken>()).Returns((bool?)null);
        var service = CreateService(db, cache: cache, helix: helix);

        Assert.Equal(VoteEligibilityResult.RoleNotEligible, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await cache.DidNotReceive().SetIsSubscriberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_RejectsWithoutCallingHelix_WhenNoUsableTokenExists()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "eligsubnotoken1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Subs);
        var cache = Substitute.For<IModRoleCache>();
        var helix = Substitute.For<ITwitchHelixClient>();
        var service = CreateService(db, cache: cache, helix: helix, tokens: TokenService(null));

        Assert.Equal(VoteEligibilityResult.RoleNotEligible, await service.EvaluateAsync(Principal(), channel.ChannelName, session.Id));

        await helix.DidNotReceive().GetUserSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetIsSubscriberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_NormalizesTheChannelName_BeforeLookingTheSessionUp()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "elignormalize1");
        var session = await SeedSessionAsync(db, channel.Id, AllowedRoles.Everyone);
        var service = CreateService(db);

        Assert.Equal(VoteEligibilityResult.Allowed, await service.EvaluateAsync(Principal(), "  ELIGnormalize1  ", session.Id));
    }

    private static TwitchPrincipalInfo Principal() => new("42", "someviewer", AccessToken: null);

    private static ITwitchUserTokenService TokenService(string? accessToken)
    {
        var tokens = Substitute.For<ITwitchUserTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new TwitchUserTokenResult(accessToken, ReauthRequired: accessToken is null));
        return tokens;
    }

    private static VoteEligibilityService CreateService(
        AppDbContext db,
        IChannelAccessService? access = null,
        IModRoleCache? cache = null,
        ITwitchHelixClient? helix = null,
        ITwitchUserTokenService? tokens = null)
    {
        return new VoteEligibilityService(
            db,
            access ?? Substitute.For<IChannelAccessService>(),
            cache ?? Substitute.For<IModRoleCache>(),
            helix ?? Substitute.For<ITwitchHelixClient>(),
            tokens ?? TokenService("valid-token"),
            NullLogger<VoteEligibilityService>.Instance);
    }

    // Channel.TwitchChannelId carries a unique index and the Postgres fixture is shared across the
    // whole collection, so every seeded channel needs its own id rather than a fixed literal.
    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName, bool withTwitchId = true)
    {
        var channel = new Channel
        {
            ChannelName = channelName,
            TwitchChannelId = withTwitchId ? Guid.NewGuid().ToString("N")[..16] : null,
            IsBotActive = true
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private static async Task<VoteSession> SeedSessionAsync(AppDbContext db, string channelId, AllowedRoles roles, bool isActive = true)
    {
        var session = new VoteSession
        {
            ChannelId = channelId,
            Title = "Test Session",
            AllowedVoterRoles = roles,
            IsActive = isActive
        };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}
