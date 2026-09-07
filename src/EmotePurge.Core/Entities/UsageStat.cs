namespace EmotePurge.Core.Entities;

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // UTC calendar day; one row per emote per day.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }

    // A row stays (EmoteId, Date)-unique regardless of these columns: a row can carry UseCount = 0
    // while BotUseCount > 0 (an emote that only a bot posted in this batch) or while
    // SharedChatUseCount > 0 (an emote that only foreign-room messages posted in this batch) —
    // the read queries in UsageStatQueryService filter on UseCount > 0 to keep a bot-only row
    // from reading as "used". A shared-only row is different until Zug 2 (issue #73, DECISIONS
    // 2026-09-06 "Shared Chat bekommt eine dritte Spalte"): the same read queries sum and filter
    // on UseCount + SharedChatUseCount instead, so it reads as used — a deliberate bridge, not an
    // oversight.
    public int BotUseCount { get; set; }

    // Everything mirrored in from a foreign room during a Twitch Shared Chat session — bots and
    // undeterminable messages included (DECISIONS 2026-09-06 "Shared Chat bekommt eine dritte
    // Spalte", #73).
    public int SharedChatUseCount { get; set; }

    public Emote Emote { get; set; } = null!;
}
