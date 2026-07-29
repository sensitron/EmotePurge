namespace EmotePurge.Core.Entities;

public class User
{
    public string Id { get; set; } = string.Empty; // Twitch User ID
    public string TwitchUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;

    // Sessions issued before this instant are rejected on the next request. Set by logout, which
    // otherwise only deletes the browser's copy of the cookie while the cookie itself stays
    // cryptographically valid — with Data Protection keys persisted across redeploys there would be
    // nothing left that can invalidate a stolen cookie for its entire lifetime.
    public DateTime? SessionsValidFromUtc { get; set; }
}
