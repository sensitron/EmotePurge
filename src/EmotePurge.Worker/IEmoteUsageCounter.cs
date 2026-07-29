namespace EmotePurge.Worker;

public interface IEmoteUsageCounter
{
    void Increment(string emoteId);

    // Puts a failed flush batch back into the counter. DrainAndReset empties it before the flush is
    // even attempted, so without this every failed flush is a guaranteed total loss of that window.
    void Merge(IReadOnlyDictionary<string, int> counts);

    IReadOnlyDictionary<string, int> DrainAndReset();
}
