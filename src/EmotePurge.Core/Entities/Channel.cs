namespace EmotePurge.Core.Entities;

public class Channel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TwitchChannelId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string ActiveEmoteSetId { get; set; } = string.Empty;
    public bool IsBotActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Emote> Emotes { get; set; } = new List<Emote>();
}
