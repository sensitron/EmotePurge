using EmotePurge.Core.Twitch;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: pure string logic. This is the one place where anything in this codebase makes an
// assumption about the shape of a Twitch CDN URL, which is exactly why it has a test that pins the
// fallback: an unrecognised shape must pass through untouched rather than break the avatar.
public class TwitchProfileImageTests
{
    [Fact]
    public void ToAvatarSize_ReplacesTheDefaultSizeMarker()
    {
        const string url = "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-300x300.png";

        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png",
            TwitchProfileImage.ToAvatarSize(url));
    }

    [Fact]
    public void ToAvatarSize_LeavesAnUnknownShapeUntouched()
    {
        // If Twitch ever changes the URL form, the avatar must still load — just larger than needed.
        const string url = "https://static-cdn.jtvnw.net/user-default-pictures-uv/some-guid.png";

        Assert.Equal(url, TwitchProfileImage.ToAvatarSize(url));
    }

    [Fact]
    public void ToAvatarSize_IsIdempotent()
    {
        const string url = "https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-70x70.png";

        Assert.Equal(url, TwitchProfileImage.ToAvatarSize(url));
    }
}
