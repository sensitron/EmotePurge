namespace EmotePurge.Core.Twitch;

/// <summary>
/// Twitch serves avatars at 300x300 and encodes the size in the file name. The header renders them
/// at 32 CSS px, so the default costs roughly an order of magnitude more bytes than it can show, on
/// every page load. 70 px covers 32 px at a device pixel ratio of 2.
/// </summary>
public static class TwitchProfileImage
{
    private const string DefaultSizeMarker = "-300x300";
    private const string AvatarSizeMarker = "-70x70";

    /// <summary>
    /// Guarded on purpose: <see cref="string.Replace(string, string, StringComparison)"/> returns
    /// the input unchanged when the marker is absent, so an unrecognised URL form falls through
    /// softly instead of breaking. This is the only assumption this codebase makes about the shape
    /// of a Twitch CDN URL.
    /// </summary>
    public static string ToAvatarSize(string url) =>
        url.Replace(DefaultSizeMarker, AvatarSizeMarker, StringComparison.Ordinal);
}
