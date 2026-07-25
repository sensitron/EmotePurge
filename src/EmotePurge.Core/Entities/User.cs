namespace EmotePurge.Core.Entities;

public class User
{
    public string Id { get; set; } = string.Empty; // Twitch User ID
    public string TwitchUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
}
