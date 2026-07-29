using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;

namespace EmotePurge.Worker;

public class TwitchChatManager(
    ILogger<TwitchChatManager> logger,
    ILoggerFactory loggerFactory,
    IEmoteMatchCache emoteMatchCache,
    IEmoteUsageCounter usageCounter) : ITwitchChatManager
{
    // Live beobachtet 2026-07-26: Nach einem "Fatal network error" in OnConnectionError blieb
    // die zugrunde liegende TwitchLib-WebSocket-Verbindung dauerhaft in einem kaputten Zustand,
    // aus dem ReconnectAsync() auf demselben Client-Objekt nie mehr herauskam (>45 Min. Ausfall,
    // jeder Reconnect-Versuch scheiterte identisch, obwohl TCP/DNS/TLS nachweislich intakt waren).
    // Ab dieser Zahl aufeinanderfolgender Fehler wird der komplette TwitchClient neu instanziiert
    // statt nur reconnectet.
    private const int MaxConsecutiveConnectionErrorsBeforeRecreate = 3;

    // Live beobachtet 2026-07-27, zwei unabhängige Umgebungen (lokal + VPS): der erste
    // "Fatal network error" trat in beiden Läufen exakt beim 10. erzwungenen Reconnect derselben
    // anonymen justinfanXXXXX-Identität auf (unterschiedliche Gesamtlaufzeit, aber identische
    // Versuchsanzahl) — spricht für ein zählbasiertes Twitch-seitiges Limit pro Identität, nicht
    // für ein Zeitfenster. Recreate erzeugt eine neue zufällige Identität und setzt den Zähler
    // zurück. Schwelle mit Sicherheitsmarge unter dem beobachteten Wert, um den Fehler proaktiv
    // zu vermeiden statt ihn erst über 3 Fehlversuche (s. o.) abzufangen.
    private const int MaxReconnectsBeforeProactiveRecreate = 8;

    private readonly ConcurrentDictionary<string, byte> _joinedChannels = new();
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private TwitchClient _client = new(loggerFactory: loggerFactory);
    private bool _connected;
    private volatile bool _isConnected;
    private long _lastMessageReceivedUtcTicks;
    private int _consecutiveConnectionErrors;
    private int _reconnectCountSinceCreation;

    public bool IsConnected => _isConnected;

    public DateTime? LastMessageReceivedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMessageReceivedUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public void Initialize()
    {
        WireUpClient(_client);
    }

    public async Task ConnectAsync()
    {
        await _client.ConnectAsync();
        _connected = true;
    }

    public async Task ForceReconnectAsync()
    {
        await _reconnectLock.WaitAsync();
        try
        {
            if (_consecutiveConnectionErrors >= MaxConsecutiveConnectionErrorsBeforeRecreate)
            {
                await RecreateClientAsync(
                    $"{_consecutiveConnectionErrors} aufeinanderfolgende Verbindungsfehler erreicht (Schwelle {MaxConsecutiveConnectionErrorsBeforeRecreate}).");
                return;
            }

            if (_reconnectCountSinceCreation >= MaxReconnectsBeforeProactiveRecreate)
            {
                await RecreateClientAsync(
                    $"proaktive Schwelle erreicht: {_reconnectCountSinceCreation} Reconnects seit letzter Client-Erzeugung (Schwelle {MaxReconnectsBeforeProactiveRecreate}).");
                return;
            }

            logger.LogInformation(
                "Erzwinge Reconnect (Versuch Nr. {Count} seit letzter Client-Erzeugung).",
                _reconnectCountSinceCreation + 1);

            // OnReconnected rejoint bereits alle _joinedChannels — kein Duplizieren der Rejoin-Logik nötig.
            await _client.ReconnectAsync();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task RecreateClientAsync(string reason)
    {
        logger.LogWarning(
            "TwitchClient wird komplett neu instanziiert statt nur reconnectet. Grund: {Reason}",
            reason);

        var oldClient = _client;
        UnwireClient(oldClient);
        try
        {
            await oldClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aufräumen des alten TwitchClient beim Neu-Erstellen fehlgeschlagen (ignoriert).");
        }

        var newClient = new TwitchClient(loggerFactory: loggerFactory);
        WireUpClient(newClient);
        _client = newClient;
        _consecutiveConnectionErrors = 0;
        _reconnectCountSinceCreation = 0;

        await _client.ConnectAsync();

        // ConnectAsync() auf einem frischen Client feuert OnConnected, nicht OnReconnected —
        // die Rejoin-Schleife lebt aber nur in OnReconnected. Live beobachtet 2026-07-27: Ohne
        // diesen expliziten Aufruf blieb der Worker nach einem Recreate mit Twitch IRC verbunden,
        // aber in null Channels gejoint, bis zufällig der nächste Watchdog-Zyklus einen normalen
        // ReconnectAsync() (statt erneutem Recreate) auslöste.
        await RejoinTrackedChannelsAsync();
    }

    private void WireUpClient(TwitchClient client)
    {
        client.Initialize(new ConnectionCredentials()); // anonym/read-only
        client.OnConnected += OnConnected;
        client.OnReconnected += OnReconnected;
        client.OnDisconnected += OnDisconnected;
        client.OnFailureToReceiveJoinConfirmation += OnFailureToReceiveJoinConfirmation;
        client.OnConnectionError += OnConnectionError;
        client.OnJoinedChannel += OnJoinedChannel;
        client.OnLeftChannel += OnLeftChannel;
        client.OnMessageReceived += OnMessageReceived;
    }

    private void UnwireClient(TwitchClient client)
    {
        client.OnConnected -= OnConnected;
        client.OnReconnected -= OnReconnected;
        client.OnDisconnected -= OnDisconnected;
        client.OnFailureToReceiveJoinConfirmation -= OnFailureToReceiveJoinConfirmation;
        client.OnConnectionError -= OnConnectionError;
        client.OnJoinedChannel -= OnJoinedChannel;
        client.OnLeftChannel -= OnLeftChannel;
        client.OnMessageReceived -= OnMessageReceived;
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
            _joinedChannels[channelName] = 0;
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
        finally
        {
            _joinedChannels.TryRemove(channelName, out _);
        }
    }

    private Task OnConnected(object? sender, OnConnectedEventArgs e)
    {
        _isConnected = true;
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
        logger.LogInformation("TwitchClient verbunden.");
        return Task.CompletedTask;
    }

    private Task OnDisconnected(object? sender, OnDisconnectedArgs e)
    {
        // TwitchLib rejoint Channels nach einem Reconnect NICHT automatisch — ohne
        // sichtbares Log hier würde ein stiller Verbindungsabbruch (z. B. Twitch-seitiges
        // PING-Timeout) das Chat-Matching für alle Channels lautlos einfrieren.
        _isConnected = false;
        logger.LogWarning("TwitchClient getrennt.");
        return Task.CompletedTask;
    }

    private Task OnFailureToReceiveJoinConfirmation(object? sender, OnFailureToReceiveJoinConfirmationArgs e)
    {
        // Ohne dieses Log bliebe ein Channel, dessen JOIN Twitch nie bestätigt, dauerhaft
        // stumm — auch nach einem Reconnect, da nur bereits erfolgreich gejointe Channels
        // (s. _joinedChannels) für den Rejoin getrackt werden.
        logger.LogWarning(
            "Twitch hat den Join für {Channel} nicht bestätigt. Details: {Details}",
            e.Exception.Channel, e.Exception.Details);
        return Task.CompletedTask;
    }

    private Task OnConnectionError(object? sender, OnConnectionErrorArgs e)
    {
        var count = Interlocked.Increment(ref _consecutiveConnectionErrors);
        // e.Error ist ein TwitchLib-eigener ErrorEvent (kein Exception), enthält also nur diese
        // Message — keine tiefere Diagnose (Socket/TLS) über dieses Event allein möglich. Der
        // Fehlerzähler wird deshalb jetzt zumindest mitgeloggt, um beim nächsten Vorfall ohne
        // Rätselraten zu sehen, wie nah ein Recreate ist.
        logger.LogWarning(
            "TwitchClient-Verbindungsfehler für {BotUsername} ({Count}/{Max} aufeinanderfolgend): {Error}",
            e.BotUsername, count, MaxConsecutiveConnectionErrorsBeforeRecreate, e.Error.Message);
        return Task.CompletedTask;
    }

    private async Task OnReconnected(object? sender, OnConnectedEventArgs e)
    {
        _isConnected = true;
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
        var count = Interlocked.Increment(ref _reconnectCountSinceCreation);
        logger.LogInformation(
            "TwitchClient reconnected (Reconnect Nr. {Count} seit letzter Client-Erzeugung, Schwelle für proaktives Recreate: {Max}).",
            count, MaxReconnectsBeforeProactiveRecreate);
        await RejoinTrackedChannelsAsync();
    }

    private async Task RejoinTrackedChannelsAsync()
    {
        var channels = _joinedChannels.Keys.ToArray();
        logger.LogWarning("Rejoine {Count} getrackte Channel(s).", channels.Length);

        foreach (var channelName in channels)
        {
            try
            {
                await _client.JoinChannelAsync(channelName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Rejoin fehlgeschlagen für {Channel}.", channelName);
            }
        }
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
        // Aktualisiert für JEDE Nachricht, nicht nur gematchte — der Watchdog erkennt so
        // auch ein stilles Einfrieren der Verbindung auf Channels ohne Emote-Nutzung.
        Interlocked.Exchange(ref _lastMessageReceivedUtcTicks, DateTime.UtcNow.Ticks);

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
