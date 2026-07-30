using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;

namespace EmotePurge.Api.Validation;

internal static class ChannelNameValidation
{
    private static readonly Regex Pattern = new("^[a-z0-9_]{4,25}$", RegexOptions.Compiled);

    /// <summary>
    /// Format check for an inbound channel name. Normalizes through
    /// <see cref="ChannelName.Normalize"/> first, so a mixed-case Twitch login ("HandOfBlood") passes
    /// even though the pattern itself is lower-case only — that combination is deliberate, and the
    /// frontend validator has already tripped over the difference once.
    /// </summary>
    public static bool IsValid(string channelName) => Pattern.IsMatch(ChannelName.Normalize(channelName));
}
