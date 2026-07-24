namespace EmotePurge.Core.Services;

public interface IEmoteMatchCache
{
    void ReplaceChannel(string channelName, IReadOnlyDictionary<string, string> emoteNameToId);

    void RemoveChannel(string channelName);

    IReadOnlyDictionary<string, string> GetChannelEmotes(string channelName);
}
