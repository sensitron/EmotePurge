namespace EmotePurge.Core.Entities;

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // UTC calendar day; one row per emote per day.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }

    public Emote Emote { get; set; } = null!;
}
