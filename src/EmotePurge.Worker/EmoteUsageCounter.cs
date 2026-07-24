using System.Collections.Concurrent;

namespace EmotePurge.Worker;

public class EmoteUsageCounter : IEmoteUsageCounter
{
    private ConcurrentDictionary<string, int> _counts = new();

    public void Increment(string emoteId)
        => _counts.AddOrUpdate(emoteId, 1, (_, current) => current + 1);

    public IReadOnlyDictionary<string, int> DrainAndReset()
        => Interlocked.Exchange(ref _counts, new ConcurrentDictionary<string, int>());
}
