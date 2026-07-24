namespace EmotePurge.Core.Entities;

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // UTC calendar day (time component always 00:00:00); one row per emote per day.
    public DateTime Date { get; set; }
    public int UseCount { get; set; }

    public Emote Emote { get; set; } = null!;
}
