using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// IsGlobalAdmin is a pure configuration lookup — no container, no database, no Twitch call. The
// collaborators are substituted only because the primary constructor demands them; none is touched
// on this path.
//
// The precedence assertions are the point: the admin allow-list used to be readable only from the
// appsettings.json array, which meant it could not be overridden in Docker at all (see the
// 2026-08-01 review, WB-1). An environment variable can only ever set the scalar key, so the
// scalar has to win over the indexed keys the JSON array produces.
public class ChannelAccessServiceAdminTests
{
    [Fact]
    public void IsGlobalAdmin_ReadsTheAppsettingsArray_WhenNoScalarIsSet()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:AdminTwitchLogins:0"] = "alice",
            ["Auth:AdminTwitchLogins:1"] = "bob",
        });

        Assert.True(service.IsGlobalAdmin(Principal("bob")));
        Assert.False(service.IsGlobalAdmin(Principal("carol")));
    }

    [Fact]
    public void IsGlobalAdmin_LetsTheScalarOverrideTheArray()
    {
        // Both shapes present at once — exactly what an environment variable does on top of
        // appsettings.json, because they land on different configuration keys.
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:AdminTwitchLogins:0"] = "alice",
            ["Auth:AdminTwitchLogins"] = "carol,dave",
        });

        Assert.False(service.IsGlobalAdmin(Principal("alice")));
        Assert.True(service.IsGlobalAdmin(Principal("carol")));
        Assert.True(service.IsGlobalAdmin(Principal("dave")));
    }

    [Theory]
    [InlineData("carol, dave")]
    [InlineData(" carol ,dave,")]
    [InlineData("carol,,dave")]
    public void IsGlobalAdmin_TrimsAndSkipsEmptyEntries(string configured)
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:AdminTwitchLogins"] = configured,
        });

        Assert.True(service.IsGlobalAdmin(Principal("carol")));
        Assert.True(service.IsGlobalAdmin(Principal("dave")));
        Assert.False(service.IsGlobalAdmin(Principal("")));
    }

    [Fact]
    public void IsGlobalAdmin_MatchesCaseInsensitively()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:AdminTwitchLogins"] = "HandOfBlood",
        });

        Assert.True(service.IsGlobalAdmin(Principal("handofblood")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsGlobalAdmin_GrantsNothing_WhenTheAllowListIsBlank(string configured)
    {
        // A blank ADMIN_TWITCH_LOGINS in .env must not fall back to "everyone" — an unset
        // allow-list is the closed state, not the open one.
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:AdminTwitchLogins"] = configured,
        });

        Assert.False(service.IsGlobalAdmin(Principal("alice")));
    }

    [Fact]
    public void IsGlobalAdmin_GrantsNothing_WhenNothingIsConfigured()
    {
        var service = CreateService([]);

        Assert.False(service.IsGlobalAdmin(Principal("alice")));
    }

    private static TwitchPrincipalInfo Principal(string login) => new("12345", login, AccessToken: null);

    private static ChannelAccessService CreateService(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ChannelAccessService(
            Substitute.For<IModeratorCheckService>(),
            Substitute.For<ISevenTvEditorService>(),
            Substitute.For<IChannelService>(),
            configuration,
            NullLogger<ChannelAccessService>.Instance);
    }
}
