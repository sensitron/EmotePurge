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
    // Bounds how long we *wait* for a connect/reconnect, not how long TwitchLib tries: the
    // reconnection policy retries indefinitely in the background and still raises
    // OnConnected/OnReconnected when it gets through. Without this bound a Twitch outage during
    // startup would block Worker.ExecuteAsync — no Redis subscription, no join/leave commands.
    private static readonly TimeSpan OpenWaitTimeout = TimeSpan.FromSeconds(30);

    // Twitch permits 20 joins per 10 seconds on a non-verified connection, and TwitchLib paces them
    // not at all: JOINs bypass its ThrottlingService (that one only covers chat messages) and its
    // queue advances as fast as confirmations arrive — about 180ms per channel, measured on prod.
    // Twenty channels back to back therefore sit exactly on the limit and anything above it exceeds
    // it, which costs the excess channels their join until EnsureJoinedAsync retries them. 600ms
    // keeps every join path we control at roughly 16 per 10 seconds.
    //
    // This cannot cover TwitchLib's own rejoin after a reconnect, which happens inside the library
    // and bursts through all channels at full speed. Above ~20 channels that needs either sharding
    // across several clients or a verified bot account — see the scaling note in
    // docs/Review-2026-07-29-Umsetzung.md.
    private static readonly TimeSpan MinIntervalBetweenJoins = TimeSpan.FromMilliseconds(600);

    // Desired channels, not confirmed ones — the value records whether Twitch confirmed the JOIN.
    // Tracking intent instead of success is what makes a failed join retryable (see TryJoinAsync
    // and EnsureJoinedAsync).
    private readonly ConcurrentDictionary<string, bool> _desiredChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _joinGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    // Decides reconnect vs. recreate vs. wait, and owns the two counters that decision rests on.
    // Deliberately not injected: it is pure state belonging to this manager, not a dependency.
    private readonly ReconnectPolicy _reconnectPolicy = new();

    private TwitchClient _client = CreateClient(loggerFactory);
    private volatile bool _isConnected;
    private long _lastMessageReceivedUtcTicks;
    private long _connectAttemptedUtcTicks;
    private int _joinsIssuedForCurrentClient;
    private DateTime _lastJoinIssuedUtc = DateTime.MinValue;

    public bool IsConnected => _isConnected;

    public DateTime? LastMessageReceivedUtc => ReadTimestamp(ref _lastMessageReceivedUtcTicks);

    public DateTime? ConnectAttemptedUtc => ReadTimestamp(ref _connectAttemptedUtcTicks);

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
            var openRunningFor = ConnectAttemptedUtc is { } attemptedAt ? DateTime.UtcNow - attemptedAt : (TimeSpan?)null;
            var decision = _reconnectPolicy.Decide(openRunningFor);

            switch (decision.Action)
            {
                case ReconnectAction.Wait:
                    logger.LogInformation("{Reason}", decision.Reason);
                    return;
                case ReconnectAction.Recreate:
                    await RecreateClientAsync(decision.Reason);
                    return;
                default:
                    logger.LogInformation("Erzwinge Reconnect. Grund: {Reason}", decision.Reason);
                    await ReconnectClientAsync();
                    return;
            }
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
            _reconnectPolicy.RegisterOpenSettled();

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
            _reconnectPolicy.RegisterOpenSettled();
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
        _reconnectPolicy.RegisterOpenStarted();
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
            _reconnectPolicy.RegisterOpenSettled();
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
        _reconnectPolicy.RegisterOpenSettled();
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
        _reconnectPolicy.RegisterClientReplaced();
        // The new client knows nothing about our channels, so its first OnConnected must rejoin.
        Interlocked.Exchange(ref _joinsIssuedForCurrentClient, 0);

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

        // Serialises and paces every join we issue, across all callers: boot recovery, the rejoin
        // after a recreate, Redis join commands and the periodic EnsureJoinedAsync sweep would
        // otherwise each burst independently. A single ad-hoc join is unaffected whenever the
        // previous one is long enough ago.
        await _joinGate.WaitAsync();
        try
        {
            var sinceLastJoin = DateTime.UtcNow - _lastJoinIssuedUtc;
            if (sinceLastJoin < MinIntervalBetweenJoins)
            {
                await Task.Delay(MinIntervalBetweenJoins - sinceLastJoin);
            }

            _lastJoinIssuedUtc = DateTime.UtcNow;
            await _client.JoinChannelAsync(channelName);
        }
        catch (Exception ex)
        {
            // Must not abort the caller's loop (boot recovery, rejoin, periodic resync).
            logger.LogWarning(ex, "Join fehlgeschlagen für {Channel} — wird beim nächsten Reconnect nachgeholt.", channelName);
        }
        finally
        {
            _joinGate.Release();
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
        _reconnectPolicy.RegisterConnected();
        logger.LogInformation("TwitchClient verbunden.");

        // Only a client that has not joined anything yet needs us. Verified in TwitchClient 4.0.1
        // (_client_OnReconnected): on a reconnect of the *same* client TwitchLib re-enqueues its own
        // joined channels, joins them, and only then clears its list and raises the event — so it
        // rejoins by itself, and because that clear happens first, JoinChannelAsync's own duplicate
        // check no longer suppresses anything we send afterwards. Observed on prod 2026-07-29: twelve
        // join confirmations for six channels after a single reconnect. A fresh client (first boot,
        // or after a recreate) has an empty list instead, so there nothing would ever be joined
        // without us — that case, and only that case, is handled here. Raised by Handle004, i.e. once
        // per completed IRC handshake, which is also why this fires after a reconnect at all.
        if (Interlocked.Exchange(ref _joinsIssuedForCurrentClient, 1) == 0)
        {
            await RejoinDesiredChannelsAsync();
        }
    }

    private Task OnDisconnected(object? sender, OnDisconnectedArgs e)
    {
        // Without a log line here a silent drop (a Twitch-side PING timeout, say) would freeze chat
        // matching for every channel without a trace. Marking the channels unconfirmed lets
        // EnsureJoinedAsync verify them again — TwitchLib rejoins them itself on reconnect, but only
        // those its own list happens to hold, not the ones we wanted and never got.
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
        var count = _reconnectPolicy.RegisterConnectionError();
        // e.Error ist ein TwitchLib-eigener ErrorEvent (kein Exception), enthält also nur diese
        // Message — keine tiefere Diagnose (Socket/TLS) über dieses Event allein möglich. Der
        // Fehlerzähler wird deshalb jetzt zumindest mitgeloggt, um beim nächsten Vorfall ohne
        // Rätselraten zu sehen, wie nah ein Recreate ist.
        logger.LogWarning(
            "TwitchClient-Verbindungsfehler für {BotUsername} ({Count}/{Max} aufeinanderfolgend): {Error}",
            e.BotUsername, count, ReconnectPolicy.MaxConsecutiveConnectionErrors, e.Error.Message);
        return Task.CompletedTask;
    }

    private Task OnReconnected(object? sender, OnConnectedEventArgs e)
    {
        _isConnected = true;
        _reconnectPolicy.RegisterConnected();

        // No rejoin here: TwitchLib has already done it in the very code path that raises this event
        // (see OnConnected). Rejoining anyway doubled every JOIN on every reconnect — harmless at six
        // channels, but Twitch allows 20 joins per 10 seconds, and exceeding that drops the
        // connection, which is exactly what the watchdog then reacts to.
        logger.LogInformation("TwitchClient reconnected.");
        return Task.CompletedTask;
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

    private static DateTime? ReadTimestamp(ref long ticksField)
    {
        var ticks = Interlocked.Read(ref ticksField);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }
}
