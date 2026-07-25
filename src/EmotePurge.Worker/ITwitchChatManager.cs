namespace EmotePurge.Worker;

public interface ITwitchChatManager
{
    void Initialize();

    Task ConnectAsync();

    Task JoinChannelAsync(string channelName);

    Task LeaveChannelAsync(string channelName);

    // Für den Watchdog (s. TwitchConnectionWatchdog): erkennt stille Verbindungsabbrüche,
    // bei denen TwitchLib selbst kein OnDisconnected feuert.
    bool IsConnected { get; }

    DateTime? LastMessageReceivedUtc { get; }

    Task ForceReconnectAsync();
}
