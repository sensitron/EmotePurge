using System.Text.RegularExpressions;

namespace EmotePurge.Api.Validation;

internal static class ChannelNameValidation
{
    private static readonly Regex Pattern = new("^[a-z0-9_]{4,25}$", RegexOptions.Compiled);

    public static bool IsValid(string channelName) => Pattern.IsMatch(channelName.Trim().ToLowerInvariant());
}
