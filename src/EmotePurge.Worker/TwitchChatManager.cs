using System.Collections.Concurrent;
using EmotePurge.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace EmotePurge.Worker;

public class TwitchChatManager(
    ILogger<TwitchChatManager> logger,
    ILoggerFactory loggerFactory,
    IEmoteMatchCache emoteMatchCache,
    IEmoteUsageCounter usageCounter) : ITwitchChatManager
{
    // Live observed 2026-07-26: after a "Fatal network error" the underlying TwitchLib socket
    // stayed permanently broken and ReconnectAsync() on the same client object never recovered
    // (>45 min outage). Replacing the whole client is the escape hatch. The root cause of that
    // specific outage is fixed in CreateClient below; this threshold remains as a safety net for
    // any other way a client object can end up wedged.
    private const int MaxConsecutiveConnectionErrorsBeforeRecreate = 3;

    // Bounds how long we *wait* for a connect/reconnect, not how long TwitchLib tries: the
    // reconnection policy retries indefinitely in the background and still raises
    // OnConnected/OnReconnected when it gets through. Without this bound a Twitch outage during
    // startup would block Worker.ExecuteAsync — no Redis subscription, no join/leave commands.
    private static readonly TimeSpan OpenWaitTimeout = TimeSpan.FromSeconds(30);

    // If an open loop has been running this long without ever reaching OnConnected, the client
    // object is assumed wedged and gets replaced rather than waited on any further.
    private static readonly TimeSpan StuckOpenThreshold = TimeSpan.FromMinutes(10);

    // Desired channels, not confirmed ones — the value records whether Twitch confirmed the JOIN.
    // Tracking intent instead of success is what makes a failed join retryable (see TryJoinAsync
    // and EnsureJoinedAsync).
    private readonly ConcurrentDictionary<string, bool> _desiredChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private TwitchClient _client = CreateClient(loggerFactory);
    private volatile bool _isConnected;
    private long _lastMessageReceivedUtcTicks;
    private long _connectAttemptedUtcTicks;
    private int _consecutiveConnectionErrors;
    private int _openInFlight;

    public bool IsConnected => _isConnected;

    public DateTime? LastMessageReceivedUtc => ReadTimestamp(ref _lastMessageReceivedUtcTicks);

    public DateTime? ConnectAttemptedUtc => ReadTimestamp(ref _connectAttemptedUtcTicks);

    private static TwitchClient CreateClient(ILoggerFactory loggerFactory) => new(
        client: new WebSocketClient(
            // Verified against TwitchLib.Communication 2.0.1 (commit d1904be): a null policy
            // defaults to ReconnectionPolicy(3_000, maxAttempts: 10), and Reset(isReconnect: true)
            // returns early *without* clearing _attemptsMade. Those 10 attempts are therefore a
            // budget for the client instance's entire lifetime, not per reconnect. Once spent,
            // OpenPrivateAsync skips its connect loop entirely and raises "Fatal network error."
            // forever, and TwitchLib's own ConnectionWatchDog breaks out of its monitor loop for
            // good — exactly the "fails at the 10th reconnect, in two independent environments"
            // outage. The parameterless policy has maxAttempts == null, so AreAttemptsComplete()
            // is never true: unlimited attempts with a 3s -> 30s backoff.
            new ClientOptions(new ReconnectionPolicy()),
            loggerFactory.CreateLogger<WebSocketClient>()),
        loggerFactory: loggerFactory);

    public void Initialize()
    {
        WireUpClient(_client);
    }

    public Task ConnectAsync() => OpenAsync();

    public async Task ForceReconnectAsync()
    {
        // Skip rather than queue: an open loop can now take arbitrarily long, while the watchdog
        // ticks every minute. Queueing would pile ticks up behind it and could keep the recreate
        // escape hatch locked out for as long as the loop runs.
        if (!await _reconnectLock.WaitAsync(TimeSpan.Zero))
        {
            logger.LogInformation("Ein Reconnect läuft bereits, Watchdog-Durchlauf übersprungen.");
            return;
        }

        try
        {
            if (_consecutiveConnectionErrors >= MaxConsecutiveConnectionErrorsBeforeRecreate)
            {
                await RecreateClientAsync(
                    $"{_consecutiveConnectionErrors} aufeinanderfolgende Verbindungsfehler erreicht (Schwelle {MaxConsecutiveConnectionErrorsBeforeRecreate}).");
                return;
            }

            if (_openInFlight == 1)
            {
                var openingFor = DateTime.UtcNow - (ConnectAttemptedUtc ?? DateTime.UtcNow);
                if (openingFor < StuckOpenThreshold)
                {
                    logger.LogInformation(
                        "Verbindungsaufbau läuft seit {Seconds}s noch — kein zusätzlicher Reconnect.",
                        (int)openingFor.TotalSeconds);
                    return;
                }

                await RecreateClientAsync(
                    $"Verbindungsaufbau hängt seit {(int)openingFor.TotalMinutes} Minuten ohne Erfolg.");
                return;
            }

            logger.LogInformation("Erzwinge Reconnect.");
            await ReconnectClientAsync();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task<bool> OpenAsync()
    {
        MarkOpenStarted();
        var open = _client.ConnectAsync();

        try
        {
            // ConnectAsync returns Task<bool> and signals failure by returning false rather than
            // throwing — discarding that result used to make a failed connect indistinguishable
            // from a successful one, leaving the worker "started" with no IRC connection.
            var opened = await open.WaitAsync(OpenWaitTimeout);
            Interlocked.Exchange(ref _openInFlight, 0);

            if (!opened)
            {
                logger.LogError("Initialer TwitchClient-Connect fehlgeschlagen.");
            }

            return opened;
        }
        catch (TimeoutException)
        {
            LogOpenStillRunning();
            ObserveInBackground(open);
            return false;
        }
    }

    private async Task ReconnectClientAsync()
    {
        MarkOpenStarted();

        // Unlike ConnectAsync, TwitchClient.ReconnectAsync returns a plain Task (verified against
        // ITwitchClient at tag 4.0.1) — there is no result to evaluate here, the outcome only
        // surfaces as OnReconnected or OnConnectionError, which is where the counters live.
        var reconnect = _client.ReconnectAsync();

        try
        {
            await reconnect.WaitAsync(OpenWaitTimeout);
            Interlocked.Exchange(ref _openInFlight, 0);
        }
        catch (TimeoutException)
        {
            LogOpenStillRunning();
            ObserveInBackground(reconnect);
        }
    }

    private void MarkOpenStarted()
    {
        Interlocked.Exchange(ref _connectAttemptedUtcTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _openInFlight, 1);
    }

    // Not a failure: the policy has no attempt limit, so the open loop keeps retrying in the
    // background and still raises OnConnected/OnReconnected once it gets through. We only stop
    // blocking the caller.
    private void LogOpenStillRunning() => logger.LogWarning(
        "TwitchClient-Verbindungsaufbau nach {Seconds}s noch nicht abgeschlossen — läuft im Hintergrund weiter.",
        (int)OpenWaitTimeout.TotalSeconds);

    // Keeps an abandoned open loop from becoming an unobserved task exception and makes sure the
    // in-flight marker is cleared whenever it eventually settles.
    private void ObserveInBackground(Task open) => _ = open.ContinueWith(
        completed =>
        {
            Interlocked.Exchange(ref _openInFlight, 0);
            if (completed.IsFaulted)
            {
                logger.LogWarning(
                    completed.Exception,
                    "Im Hintergrund weiterlaufender TwitchClient-Verbindungsaufbau ist mit einer Exception beendet.");
            }
        },
        TaskScheduler.Default);

    private async Task RecreateClientAsync(string reason)
    {
        logger.LogWarning(
            "TwitchClient wird komplett neu instanziiert statt nur reconnectet. Grund: {Reason}",
            reason);

        var oldClient = _client;
        UnwireClient(oldClient);

        // UnwireClient suppresses OnDisconnected, so no event will ever correct this flag again.
        // Without it the health key keeps reporting "connected" for a client we just discarded —
        // in exactly the situation where that signal matters most.
        _isConnected = false;
        Interlocked.Exchange(ref _openInFlight, 0);
        MarkAllChannelsUnconfirmed();

        try
        {
            await oldClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aufräumen des alten TwitchClient beim Neu-Erstellen fehlgeschlagen (ignoriert).");
        }

        var newClient = CreateClient(loggerFactory);
        WireUpClient(newClient);
        _client = newClient;
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);

        // No explicit rejoin here: a fresh client raises OnConnected, and that handler rejoins.
        // Doing it here as well would issue every JOIN twice and produce spurious
        // OnFailureToReceiveJoinConfirmation warnings for the duplicates.
        await OpenAsync();
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
        // Record the intent *before* attempting it. Tracking only confirmed joins meant a channel
        // whose JOIN failed — e.g. because the client happened to be mid-recreate — was never
        // retried by any reconnect: database and match cache looked correct while usage data
        // stayed empty forever, with no signal anywhere.
        _desiredChannels.AddOrUpdate(channelName, false, (_, confirmed) => confirmed);
        await TryJoinAsync(channelName);
    }

    public async Task EnsureJoinedAsync(string channelName)
    {
        // Safety net driven by the periodic resync, which enumerates all active channels anyway:
        // covers lost Redis commands, joins that failed during boot recovery, and joins Twitch
        // never confirmed.
        if (_desiredChannels.TryGetValue(channelName, out var confirmed) && confirmed)
        {
            return;
        }

        await JoinChannelAsync(channelName);
    }

    private async Task TryJoinAsync(string channelName)
    {
        if (!_isConnected)
        {
            logger.LogWarning(
                "Join für {Channel} aufgeschoben — TwitchClient ist derzeit nicht verbunden.",
                channelName);
            return;
        }

        try
        {
            await _client.JoinChannelAsync(channelName);
        }
        catch (Exception ex)
        {
            // Must not abort the caller's loop (boot recovery, rejoin, periodic resync).
            logger.LogWarning(ex, "Join fehlgeschlagen für {Channel} — wird beim nächsten Reconnect nachgeholt.", channelName);
        }
    }

    public async Task LeaveChannelAsync(string channelName)
    {
        // Drop the intent first, so nothing rejoins this channel afterwards even if the leave
        // itself fails or the client is currently disconnected.
        _desiredChannels.TryRemove(channelName, out _);

        if (!_isConnected)
        {
            logger.LogWarning(
                "Leave für {Channel} übersprungen — TwitchClient ist derzeit nicht verbunden.",
                channelName);
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

    private async Task OnConnected(object? sender, OnConnectedEventArgs e)
    {
        _isConnected = true;
        Interlocked.Exchange(ref _openInFlight, 0);
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
        logger.LogInformation("TwitchClient verbunden.");

        // Also the landing point for a connect that only succeeded after ConnectAsync() stopped
        // waiting, and for the fresh client after a recreate: both raise OnConnected rather than
        // OnReconnected, and both need the desired channels joined.
        await RejoinDesiredChannelsAsync();
    }

    private Task OnDisconnected(object? sender, OnDisconnectedArgs e)
    {
        // TwitchLib rejoint Channels nach einem Reconnect NICHT automatisch — ohne
        // sichtbares Log hier würde ein stiller Verbindungsabbruch (z. B. Twitch-seitiges
        // PING-Timeout) das Chat-Matching für alle Channels lautlos einfrieren.
        _isConnected = false;
        MarkAllChannelsUnconfirmed();
        logger.LogWarning("TwitchClient getrennt.");
        return Task.CompletedTask;
    }

    private Task OnFailureToReceiveJoinConfirmation(object? sender, OnFailureToReceiveJoinConfirmationArgs e)
    {
        // The channel stays in _desiredChannels as unconfirmed, so EnsureJoinedAsync retries it
        // on the next periodic resync instead of leaving it silently muted.
        logger.LogWarning(
            "Twitch hat den Join für {Channel} nicht bestätigt. Details: {Details}",
            e.Exception.Channel, e.Exception.Details);
        return Task.CompletedTask;
    }

    private Task OnConnectionError(object? sender, OnConnectionErrorArgs e)
    {
        Interlocked.Exchange(ref _openInFlight, 0);
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
        Interlocked.Exchange(ref _openInFlight, 0);
        Interlocked.Exchange(ref _consecutiveConnectionErrors, 0);
        logger.LogInformation("TwitchClient reconnected.");
        await RejoinDesiredChannelsAsync();
    }

    private async Task RejoinDesiredChannelsAsync()
    {
        var channels = _desiredChannels.Keys.ToArray();
        if (channels.Length == 0)
        {
            return;
        }

        logger.LogInformation("Rejoine {Count} gewünschte(n) Channel(s).", channels.Length);

        foreach (var channelName in channels)
        {
            await TryJoinAsync(channelName);
        }
    }

    private void MarkAllChannelsUnconfirmed()
    {
        foreach (var channelName in _desiredChannels.Keys)
        {
            _desiredChannels.TryUpdate(channelName, false, true);
        }
    }

    private static DateTime? ReadTimestamp(ref long ticksField)
    {
        var ticks = Interlocked.Read(ref ticksField);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    private Task OnJoinedChannel(object? sender, OnJoinedChannelArgs e)
    {
        // Only a confirmed join stops EnsureJoinedAsync from retrying it every minute. TryUpdate
        // deliberately does not insert: a confirmation arriving after a leave must not resurrect
        // the channel as desired.
        _desiredChannels.TryUpdate(e.Channel, true, false);
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
