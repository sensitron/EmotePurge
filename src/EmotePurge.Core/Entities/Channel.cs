namespace EmotePurge.Core.Entities;

public class Channel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TwitchChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ActiveEmoteSetId { get; set; } = string.Empty;

    // Slot limit of the active set, as 7TV reports it — a property of the *set*, not the channel,
    // which is why it sits next to ActiveEmoteSetId and is written only where that id is written.
    // Null means 7TV did not report one; consumers must then show no budget rather than assume 1000
    // (7TV subscribers get larger sets).
    public int? ActiveEmoteSetCapacity { get; set; }
    public bool IsBotActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // When the bot last (re-)entered the channel, set only on a join that actually reactivated it.
    // CreatedAt alone would overstate the coverage of our usage data: LeaveAsync keeps the row, so a
    // channel that was left for two months and rejoined yesterday still carries its original
    // CreatedAt — and the resulting tracking gap would stay invisible to anyone reading "we count
    // since". Null means the channel was never left and rejoined, so CreatedAt is the honest answer.
    public DateTime? TrackingResumedAt { get; set; }

    // When a full 7TV REST sync last completed for this channel, whether or not it changed anything.
    // The obvious-looking alternative — MAX(Emote.LastSyncedAt) — answers a different question:
    // emote rows are only stamped when they actually change, so a channel syncing successfully every
    // minute against a set nobody edits reads as "last synced three days ago". That is the exact
    // reading a support case turns on, so the two are now separate columns rather than one number
    // doing double duty. Null means no full sync has ever completed since this column existed.
    public DateTime? LastSyncedAtUtc { get; set; }

    public ICollection<Emote> Emotes { get; set; } = new List<Emote>();
}
