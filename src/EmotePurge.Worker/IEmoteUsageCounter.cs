namespace EmotePurge.Worker;

public interface IEmoteUsageCounter
{
    void Increment(string emoteId);

    IReadOnlyDictionary<string, int> DrainAndReset();
}
