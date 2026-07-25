namespace EmotePurge.Core.Entities;

[Flags]
public enum AllowedRoles
{
    Everyone = 1,
    Subs = 2,
    VIPs = 4,
    Mods = 8,
    Broadcaster = 16
}

public class VoteSession
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public AllowedRoles AllowedVoterRoles { get; set; } = AllowedRoles.Everyone;
    public bool IsActive { get; set; } = true;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public Channel Channel { get; set; } = null!;
    public ICollection<Vote> Votes { get; set; } = new List<Vote>();
}
