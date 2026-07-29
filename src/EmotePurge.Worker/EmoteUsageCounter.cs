using System.Collections.Concurrent;

namespace EmotePurge.Worker;

public class EmoteUsageCounter : IEmoteUsageCounter
{
    private ConcurrentDictionary<string, int> _counts = new();

    public void Increment(string emoteId)
        => _counts.AddOrUpdate(emoteId, 1, (_, current) => current + 1);

    public void Merge(IReadOnlyDictionary<string, int> counts)
    {
        foreach (var (emoteId, count) in counts)
        {
            _counts.AddOrUpdate(emoteId, count, (_, current) => current + count);
        }
    }

    public IReadOnlyDictionary<string, int> DrainAndReset()
        => Interlocked.Exchange(ref _counts, new ConcurrentDictionary<string, int>());
}
