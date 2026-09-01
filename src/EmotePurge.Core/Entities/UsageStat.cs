namespace EmotePurge.Core.Entities;

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // UTC calendar day; one row per emote per day.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }

    // A row stays (EmoteId, Date)-unique regardless of this column: a row can carry UseCount = 0
    // while BotUseCount > 0 (an emote that only a bot posted in this batch) — the read queries in
    // UsageStatQueryService filter on UseCount > 0 to keep such a row from reading as "used".
    public int BotUseCount { get; set; }

    public Emote Emote { get; set; } = null!;
}
