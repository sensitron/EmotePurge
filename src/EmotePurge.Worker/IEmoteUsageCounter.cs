namespace EmotePurge.Worker;

public interface IEmoteUsageCounter
{
    void Increment(string emoteId);

    // Puts a failed flush batch back into the counter. DrainAndReset empties it before the flush is
    // even attempted, so without this every failed flush is a guaranteed total loss of that window.
    void Merge(IReadOnlyDictionary<string, int> counts);

    IReadOnlyDictionary<string, int> DrainAndReset();

    // Number of *distinct emotes* currently buffered, not the sum of their hit counts — the sum
    // would mean walking the whole dictionary on every health publish, while the entry count is a
    // cheap O(1)-ish read. As a health signal the two say the same thing: a number that keeps
    // growing across publishes means the flush is not draining.
    int PendingEmoteCount { get; }
}
