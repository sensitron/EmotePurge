namespace EmotePurge.Worker;

public interface ITwitchChatManager
{
    void Initialize();

    Task ConnectAsync();

    Task JoinChannelAsync(string channelName);

    Task LeaveChannelAsync(string channelName);
}
