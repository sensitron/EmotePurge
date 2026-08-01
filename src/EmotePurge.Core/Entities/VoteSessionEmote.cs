namespace EmotePurge.Core.Entities;

// Membership row of a session's explicit ballot. A session with no rows covers all non-archived
// channel emotes dynamically (pre-subset behavior, also the "whole set" mode); a session with rows
// is a fixed ballot chosen at creation and never edited afterwards. EmoteId is the local Emote
// guid, not the 7TV id — same convention as Vote.EmoteId.
public class VoteSessionEmote
{
    public long VoteSessionId { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    public VoteSession VoteSession { get; set; } = null!;
    public Emote Emote { get; set; } = null!;
}
