namespace EmotePurge.Worker;

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

    Task ForceReconnectAsync();
}
