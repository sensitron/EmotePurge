using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

public class EmoteMatchCache : IEmoteMatchCache
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _byChannel = new();

    public void ReplaceChannel(string channelName, IReadOnlyDictionary<string, string> emoteNameToId)
        => _byChannel[Normalize(channelName)] = emoteNameToId;

    public void RemoveChannel(string channelName)
        => _byChannel.TryRemove(Normalize(channelName), out _);

    public IReadOnlyDictionary<string, string> GetChannelEmotes(string channelName)
        => _byChannel.TryGetValue(Normalize(channelName), out var emotes) ? emotes : Empty;

    private static string Normalize(string channelName) => channelName.Trim().ToLowerInvariant();
}
