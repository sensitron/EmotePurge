using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class EmoteUsageCounter : IEmoteUsageCounter
{
    private ConcurrentDictionary<string, EmoteUsageCounts> _counts = new();

    // The TArg overload of AddOrUpdate is used deliberately: a plain closure over `isBot` would
    // allocate on every call, and this runs once per matched emote per chat message. Passing
    // `isBot` as the factory argument keeps both lambdas static, so Increment allocates nothing
    // beyond the dictionary's own first insert per emote.
    public void Increment(string emoteId, bool isBot)
        => _counts.AddOrUpdate(
            emoteId,
            static (_, isBotHit) => isBotHit ? new EmoteUsageCounts(Human: 0, Bot: 1) : new EmoteUsageCounts(Human: 1, Bot: 0),
            static (_, current, isBotHit) => isBotHit ? current with { Bot = current.Bot + 1 } : current with { Human = current.Human + 1 },
            isBot);

    public void Merge(IReadOnlyDictionary<string, EmoteUsageCounts> counts)
    {
        foreach (var (emoteId, addition) in counts)
        {
            // Same TArg pattern as Increment, for the same reason: `addition` travels as the
            // factory argument instead of being captured by a closure.
            _counts.AddOrUpdate(
                emoteId,
                static (_, added) => added,
                static (_, current, added) => new EmoteUsageCounts(current.Human + added.Human, current.Bot + added.Bot),
                addition);
        }
    }

    public IReadOnlyDictionary<string, EmoteUsageCounts> DrainAndReset()
        => Interlocked.Exchange(ref _counts, new ConcurrentDictionary<string, EmoteUsageCounts>());

    // Volatile.Read because DrainAndReset swaps the whole dictionary out from under concurrent
    // readers; without it this could observe a stale reference.
    public int PendingEmoteCount => Volatile.Read(ref _counts).Count;
}
