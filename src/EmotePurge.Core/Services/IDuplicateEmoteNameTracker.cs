namespace EmotePurge.Core.Services;

/// <summary>
/// Remembers per channel which duplicate active emote names were last reported, so the sync path
/// can log a collision once when it appears (and once when it resolves) instead of on every
/// match-cache refresh — those run on every resync tick.
/// </summary>
public interface IDuplicateEmoteNameTracker
{
    /// <summary>
    /// Records the channel's current set of colliding names and reports whether it differs from
    /// the previously recorded one. A channel that never had collisions reports no change for an
    /// empty set; a channel whose collisions just disappeared reports one final change.
    /// </summary>
    bool Update(string channelName, IReadOnlyCollection<string> duplicateNames);
}
