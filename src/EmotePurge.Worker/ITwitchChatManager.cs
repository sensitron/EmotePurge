namespace EmotePurge.Worker;

/// <summary>
/// One channel the manager wants to be in, plus what Twitch and the chat traffic say about it.
/// <paramref name="JoinConfirmed"/> is the desired-vs-confirmed distinction the manager already
/// tracks internally — a channel can be desired for hours without Twitch ever confirming the JOIN,
/// and that is precisely the silent failure this roster exists to surface.
/// </summary>
/// <param name="LastMessageUtc">
/// Last chat message seen in *this* channel. Null means none since the worker started, which for a
/// quiet channel is normal and for a busy one is the strongest available hint that the join is dead.
/// </param>
public readonly record struct TwitchRosterEntry(string ChannelName, bool JoinConfirmed, DateTime? LastMessageUtc);

public interface ITwitchChatManager
{
    void Initialize();

    Task ConnectAsync();

    Task JoinChannelAsync(string channelName);

    // Joins only if the channel isn't already joined-and-confirmed. Driven by the periodic 7TV
    // resync as a convergence net for lost Redis commands and joins Twitch never confirmed.
    Task EnsureJoinedAsync(string channelName);

    Task LeaveChannelAsync(string channelName);

    // Für den Watchdog (s. TwitchConnectionWatchdog): erkennt stille Verbindungsabbrüche,
    // bei denen TwitchLib selbst kein OnDisconnected feuert.
    bool IsConnected { get; }

    DateTime? LastMessageReceivedUtc { get; }

    // Fallback reference point for the watchdog: LastMessageReceivedUtc stays null until the very
    // first chat message, which used to make a worker that never connected undetectable.
    DateTime? ConnectAttemptedUtc { get; }

    // Every desired channel with its confirmation and traffic state, for the admin roster. Ordered
    // by name so the published snapshot is stable across ticks and diffs cleanly in a log.
    IReadOnlyList<TwitchRosterEntry> GetRoster();

    Task ForceReconnectAsync();
}
