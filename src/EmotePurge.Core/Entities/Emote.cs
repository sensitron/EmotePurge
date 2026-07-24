namespace EmotePurge.Core.Entities;

public class Emote
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // 7TV ObjectID (24-hex string). Not the PK: the same 7TV emote can be
    // active in multiple channels at once, so uniqueness is scoped per channel
    // via the (ChannelId, SevenTvEmoteId) index below instead of globally.
    public string SevenTvEmoteId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    public Channel Channel { get; set; } = null!;
    public ICollection<UsageStat> UsageStats { get; set; } = new List<UsageStat>();
}
