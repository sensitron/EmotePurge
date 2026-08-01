using EmotePurge.Core.Entities;

namespace EmotePurge.Worker.SevenTv;

/// <summary>One EventAPI subscription as it goes over the wire: a type plus its object_id condition.</summary>
public readonly record struct SevenTvSubscription(string Type, string ObjectId);

public readonly record struct SevenTvSubscriptionTarget(string EmoteSetId, string? SevenTvUserId);

/// <summary>
/// What one channel wants from the EventAPI and what the current session has confirmed. The user
/// subscription is optional by nature — a channel whose 7TV user id was never resolved desires only
/// the emote-set subscription, and reporting that as "not acknowledged yet" would invent a pending
/// state that will never resolve. <paramref name="SevenTvUserId"/> being null is what says so.
/// </summary>
public readonly record struct SevenTvChannelSubscriptionState(
    string ChannelName,
    string EmoteSetId,
    bool EmoteSetAcknowledged,
    string? SevenTvUserId,
    bool UserAcknowledged);

/// <summary>
/// The desired 7TV EventAPI subscription state, per channel. Desired-state-first (the review's
/// S2-3 lesson from lost Twitch joins): callers record intent here before any frame is sent, the
/// event client converges the live socket towards this state after every Hello, and acknowledgements
/// are tracked separately — a subscription is not assumed active just because it was requested.
/// Pure and clock-free so it can be unit-tested without a socket; all members lock on one object
/// because callers span the receive loop, Redis handlers and the periodic resync tick.
/// </summary>
public sealed class SevenTvSubscriptionRegistry
{
    public const string EmoteSetSubscriptionType = "emote_set.*";
    public const string UserSubscriptionType = "user.*";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SevenTvSubscriptionTarget> _desiredByChannel = new(StringComparer.Ordinal);
    private readonly HashSet<SevenTvSubscription> _acknowledged = [];

    public IReadOnlyList<string> DesiredChannels
    {
        get
        {
            lock (_lock)
            {
                return [.. _desiredByChannel.Keys];
            }
        }
    }

    public int DesiredSubscriptionCount => BuildDesiredSubscriptions().Count;

    /// <summary>Desired subscriptions the current session has not acknowledged (yet).</summary>
    public int UnacknowledgedCount
    {
        get
        {
            lock (_lock)
            {
                var desired = new HashSet<SevenTvSubscription>();
                foreach (var target in _desiredByChannel.Values)
                {
                    desired.Add(new SevenTvSubscription(EmoteSetSubscriptionType, target.EmoteSetId));
                    if (target.SevenTvUserId is { Length: > 0 } userId)
                    {
                        desired.Add(new SevenTvSubscription(UserSubscriptionType, userId));
                    }
                }

                desired.ExceptWith(_acknowledged);
                return desired.Count;
            }
        }
    }

    /// <summary>Records the channel's desired target. Returns true when this changed anything.</summary>
    public bool SetDesired(string channelName, string emoteSetId, string? sevenTvUserId)
    {
        var normalized = ChannelName.Normalize(channelName);
        var target = new SevenTvSubscriptionTarget(emoteSetId, sevenTvUserId);

        lock (_lock)
        {
            if (_desiredByChannel.TryGetValue(normalized, out var current) && current == target)
            {
                return false;
            }

            _desiredByChannel[normalized] = target;
            return true;
        }
    }

    public bool TryRemove(string channelName)
    {
        lock (_lock)
        {
            return _desiredByChannel.Remove(ChannelName.Normalize(channelName));
        }
    }

    /// <summary>
    /// Every subscription the socket should hold, deduplicated per (type, object_id): two channels
    /// sharing one active set (observed live) must yield exactly one subscription for it.
    /// </summary>
    public IReadOnlyList<SevenTvSubscription> BuildDesiredSubscriptions()
    {
        lock (_lock)
        {
            var subscriptions = new HashSet<SevenTvSubscription>();
            foreach (var target in _desiredByChannel.Values)
            {
                subscriptions.Add(new SevenTvSubscription(EmoteSetSubscriptionType, target.EmoteSetId));
                if (target.SevenTvUserId is { Length: > 0 } userId)
                {
                    subscriptions.Add(new SevenTvSubscription(UserSubscriptionType, userId));
                }
            }

            return [.. subscriptions];
        }
    }

    /// <summary>
    /// Per-channel desired-vs-acknowledged state for the admin roster. One row per channel even
    /// when two channels share an emote set: they then both read as acknowledged off the single
    /// subscription that covers them, which is the truth — neither is missing anything.
    /// </summary>
    public IReadOnlyList<SevenTvChannelSubscriptionState> GetChannelStates()
    {
        lock (_lock)
        {
            var states = new List<SevenTvChannelSubscriptionState>(_desiredByChannel.Count);
            foreach (var (channelName, target) in _desiredByChannel)
            {
                var userId = target.SevenTvUserId is { Length: > 0 } id ? id : null;
                states.Add(new SevenTvChannelSubscriptionState(
                    channelName,
                    target.EmoteSetId,
                    _acknowledged.Contains(new SevenTvSubscription(EmoteSetSubscriptionType, target.EmoteSetId)),
                    userId,
                    userId is not null &&
                    _acknowledged.Contains(new SevenTvSubscription(UserSubscriptionType, userId))));
            }

            states.Sort(static (left, right) => string.CompareOrdinal(left.ChannelName, right.ChannelName));
            return states;
        }
    }

    /// <summary>All channels whose active set this is — a dispatch fans out to each of them.</summary>
    public IReadOnlyList<string> GetChannelsForEmoteSet(string emoteSetId)
    {
        lock (_lock)
        {
            return [.. _desiredByChannel.Where(kv => kv.Value.EmoteSetId == emoteSetId).Select(kv => kv.Key)];
        }
    }

    public bool TryGetChannelForUser(string sevenTvUserId, out string channelName)
    {
        lock (_lock)
        {
            foreach (var (channel, target) in _desiredByChannel)
            {
                if (target.SevenTvUserId == sevenTvUserId)
                {
                    channelName = channel;
                    return true;
                }
            }

            channelName = string.Empty;
            return false;
        }
    }

    public void MarkAcknowledged(string type, string objectId)
    {
        lock (_lock)
        {
            _acknowledged.Add(new SevenTvSubscription(type, objectId));
        }
    }

    /// <summary>A new session confirms nothing — called on every Hello before resubscribing.</summary>
    public void ResetAcknowledgements()
    {
        lock (_lock)
        {
            _acknowledged.Clear();
        }
    }
}
