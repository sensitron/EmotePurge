using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public interface IEmoteUsageCounter
{
    void Increment(string emoteId, bool isBot);

    // Puts a failed flush batch back into the counter. DrainAndReset empties it before the flush is
    // even attempted, so without this every failed flush is a guaranteed total loss of that window.
    void Merge(IReadOnlyDictionary<string, EmoteUsageCounts> counts);

    IReadOnlyDictionary<string, EmoteUsageCounts> DrainAndReset();

    // Number of *distinct emotes* currently buffered, not the sum of their hit counts — the sum
    // would mean walking the whole dictionary on every health publish, while the entry count is a
    // cheap O(1)-ish read. As a health signal the two say the same thing: a number that keeps
    // growing across publishes means the flush is not draining. Unchanged by the human/bot split:
    // an emote seen only from bots still counts as one buffered emote.
    int PendingEmoteCount { get; }
}
