namespace EmotePurge.Core.Entities;

public enum VoteType
{
    Keep = 1,
    Delete = 2
}

public class Vote
{
    public long Id { get; set; }
    public long VoteSessionId { get; set; }
    public string EmoteId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public VoteType Type { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public VoteSession VoteSession { get; set; } = null!;
    public Emote Emote { get; set; } = null!;
    public User User { get; set; } = null!;
}
