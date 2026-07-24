using EmotePurge.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;

namespace EmotePurge.Worker;

public class TwitchChatManager(
    ILogger<TwitchChatManager> logger,
    IEmoteMatchCache emoteMatchCache,
    IEmoteUsageCounter usageCounter) : ITwitchChatManager
{
    private readonly TwitchClient _client = new();
    private bool _connected;

    public void Initialize()
    {
        _client.Initialize(new ConnectionCredentials()); // anonym/read-only
        _client.OnConnected += OnConnected;
        _client.OnJoinedChannel += OnJoinedChannel;
        _client.OnLeftChannel += OnLeftChannel;
        _client.OnMessageReceived += OnMessageReceived;
    }

    public async Task ConnectAsync()
    {
        await _client.ConnectAsync();
        _connected = true;
    }

    public async Task JoinChannelAsync(string channelName)
    {
        if (!_connected)
        {
            logger.LogWarning("JoinChannelAsync vor Connect aufgerufen für {Channel}, übersprungen.", channelName);
            return;
        }

        try
        {
            await _client.JoinChannelAsync(channelName);
        }
        catch (Exception ex)
        {
            // Ein fehlgeschlagener Join darf die Boot-Recovery-Schleife nicht abbrechen.
            logger.LogWarning(ex, "Join fehlgeschlagen für {Channel}.", channelName);
        }
    }

    public async Task LeaveChannelAsync(string channelName)
    {
        if (!_connected)
        {
            logger.LogWarning("LeaveChannelAsync vor Connect aufgerufen für {Channel}, übersprungen.", channelName);
            return;
        }

        try
        {
            await _client.LeaveChannelAsync(channelName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Leave fehlgeschlagen für {Channel}.", channelName);
        }
    }

    private Task OnConnected(object? sender, OnConnectedEventArgs e)
    {
        logger.LogInformation("TwitchClient verbunden.");
        return Task.CompletedTask;
    }

    private Task OnJoinedChannel(object? sender, OnJoinedChannelArgs e)
    {
        logger.LogInformation("Channel {Channel} gejoint.", e.Channel);
        return Task.CompletedTask;
    }

    private Task OnLeftChannel(object? sender, OnLeftChannelArgs e)
    {
        logger.LogInformation("Channel {Channel} verlassen.", e.Channel);
        return Task.CompletedTask;
    }

    private Task OnMessageReceived(object? sender, OnMessageReceivedArgs e)
    {
        logger.LogDebug("[{Channel}] {Username}: {Message}",
            e.ChatMessage.Channel, e.ChatMessage.Username, e.ChatMessage.Message);

        var channelEmotes = emoteMatchCache.GetChannelEmotes(e.ChatMessage.Channel);
        if (channelEmotes.Count == 0)
        {
            return Task.CompletedTask;
        }

        var matchedThisMessage = new HashSet<string>();
        foreach (var token in e.ChatMessage.Message.Split(' '))
        {
            if (channelEmotes.TryGetValue(token, out var emoteId) && matchedThisMessage.Add(emoteId))
            {
                usageCounter.Increment(emoteId);
            }
        }

        return Task.CompletedTask;
    }
}
