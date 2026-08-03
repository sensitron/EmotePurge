using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

public class DuplicateEmoteNameTracker : IDuplicateEmoteNameTracker
{
    private readonly ConcurrentDictionary<string, string[]> _byChannel = new();

    public bool Update(string channelName, IReadOnlyCollection<string> duplicateNames)
    {
        var key = ChannelName.Normalize(channelName);

        if (duplicateNames.Count == 0)
        {
            // An empty set is stored as absence, so left channels never linger here — and a
            // successful removal doubles as "there was a collision before", i.e. it just resolved.
            return _byChannel.TryRemove(key, out _);
        }

        var sorted = duplicateNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (_byChannel.TryGetValue(key, out var previous) && previous.SequenceEqual(sorted, StringComparer.Ordinal))
        {
            return false;
        }

        // Get-then-set instead of an atomic upsert: syncs of the same channel are serialized by
        // ChannelSyncGate, so no two updates for the same key race each other.
        _byChannel[key] = sorted;
        return true;
    }
}
