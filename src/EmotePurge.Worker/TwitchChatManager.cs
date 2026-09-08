using System.Collections.Concurrent;
using EmotePurge.Core.Chat;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace EmotePurge.Worker;

/// <summary>
/// The transport half of the Twitch connection (issue #68): it holds exactly one
/// <see cref="TwitchClient"/> at a time, wires and unwires it, stamps frames, tracks the desired
/// channels and paces every JOIN we issue. It makes no timing decision of its own — when and how
/// often to rebuild belongs to <see cref="TwitchConnectionWatchdog"/> and
/// <see cref="TwitchReconnectBackoffPolicy"/>, and TwitchLib's event handlers here only ever record
/// state and deposit a signal (design decision E1).
/// </summary>
public class TwitchChatManager(
    ILogger<TwitchChatManager> logger,
    ILoggerFactory loggerFactory,
    IEmoteMatchCache emoteMatchCache,
    IEmoteUsageCounter usageCounter,
    IBotChatterDetector botChatterDetector,
    WorkerStats stats) : ITwitchChatManager
{
    // Twitch permits 20 joins per 10 seconds on a non-verified connection, and TwitchLib paces them
    // not at all: JOINs bypass its ThrottlingService (that one only covers chat messages) and its
    // queue advances as fast as confirmations arrive — 126–231ms per channel, measured against
    // Twitch on 2026-09-08. Twenty channels back to back therefore sit on or above the limit, which
    // costs the excess channels their join until EnsureJoinedAsync retries them. 600ms keeps every
    // join path at roughly 16 per 10 seconds.
    //
    // Since the client is built with NoReconnectionPolicy there is no unthrottled path left:
    // TwitchLib never rejoins on its own any more, every JOIN this process issues goes through the
    // gate below, and the rejoin after a rebuild is just another caller of it.
    private static readonly TimeSpan MinIntervalBetweenJoins = TimeSpan.FromMilliseconds(600);

    // How long an opened socket may stay silent before we call the attempt failed. TwitchLib sends
    // the IRC handshake as soon as the WebSocket is up and Twitch answers "004" within a
    // round-trip, so ten seconds is two orders of magnitude of headroom, not a guess.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // How long the round waits for Twitch to confirm the JOINs it just sent — TwitchLib's own join
    // timeout is 5s, so anything unconfirmed after that will not arrive at all and belongs in the
    // "K offen" figure rather than in a longer wait.
    private static readonly TimeSpan JoinConfirmationTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan JoinConfirmationPollInterval = TimeSpan.FromMilliseconds(150);

    // Desired channels, not confirmed ones — the value records whether Twitch confirmed the JOIN.
    // Tracking intent instead of success is what makes a failed join retryable (see TryJoinAsync
    // and EnsureJoinedAsync).
    private readonly ConcurrentDictionary<string, bool> _desiredChannels = new(StringComparer.OrdinalIgnoreCase);

    // Per channel what _lastMessageReceivedUtcTicks is for the connection as a whole. The global
    // one answers "is the socket alive", this one answers "is *this* channel alive" — the question
    // a support case actually asks, and one the global figure hides: a single busy channel keeps it
    // fresh while thirty others sit silently unjoined. Same OrdinalIgnoreCase comparer as
    // _desiredChannels above, or `HandOfBlood` and `handofblood` become two roster rows.
    private readonly ConcurrentDictionary<string, long> _lastMessageByChannelTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _joinGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    // The one place a loss becomes a rebuild. Deliberately not injected: it is this manager's own
    // state, and the loop reaches it only through WaitForReconnectRequestAsync.
    private readonly TwitchReconnectSignalSlot _signalSlot = new();

    private volatile TwitchClient _client = CreateClient(loggerFactory);

    // Counted from 1 for the boot client and never reset: it is both the key of the signal slot's
    // generation rule and the rebuild id in every log line about a connection.
    private int _clientGeneration = 1;
    private int _connected;
    private long _lastMessageReceivedUtcTicks;
    private long _lastFrameReceivedUtcTicks;
    private long _connectAttemptedUtcTicks;

    // When the current session's handshake ("004") arrived, or 0 when there is no session. Set by
    // OnConnected, cleared when a rebuild attempt starts — which is what makes "no handshake at
    // all" (a failed attempt) distinguishable from "a session that ended" for the backoff policy.
    private long _sessionStartedUtcTicks;
    private DateTime _lastJoinIssuedUtc = DateTime.MinValue;

    // Completed by OnConnected of the client the current attempt is opening. Recreated per attempt,
    // so a late "004" of an abandoned client can never satisfy the wait of a newer one.
    private TaskCompletionSource? _handshakeSource;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public DateTime? LastMessageReceivedUtc => ReadTimestamp(ref _lastMessageReceivedUtcTicks);

    public DateTime? LastFrameReceivedUtc => ReadTimestamp(ref _lastFrameReceivedUtcTicks);

    public DateTime? ConnectAttemptedUtc => ReadTimestamp(ref _connectAttemptedUtcTicks);

    public void Initialize()
    {
        WireUpClient(_client);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        // One attempt, no background retry loop: if it fails, the signal below is what the rebuild
        // loop finds the first time it waits, and the loop owns every attempt from then on. Boot
        // recovery runs either way — joins record their intent and the first successful rebuild
        // rejoins them.
        try
        {
            var outcome = await OpenAndAwaitHandshakeAsync(_client, Volatile.Read(ref _clientGeneration), ct);
            if (outcome.HandshakeCompleted)
            {
                return;
            }

            logger.LogWarning(
                "Initialer TwitchClient-Connect fehlgeschlagen ({Reason}) — der Wiederaufbau übernimmt.",
                outcome.FailureReason);
            RequestReconnect(
                TwitchSessionEndReason.InitialConnectFailed,
                $"Boot-Connect fehlgeschlagen ({outcome.FailureReason}).");
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down while we were still opening: nothing to signal, nothing to wait
            // for. The half-open client dies with the process.
        }
    }

    public async Task JoinChannelAsync(string channelName)
    {
        // Record the intent *before* attempting it. Tracking only confirmed joins meant a channel
        // whose JOIN failed — e.g. because the client happened to be mid-rebuild — was never
        // retried by any reconnect: database and match cache looked correct while usage data
        // stayed empty forever, with no signal anywhere.
        _desiredChannels.AddOrUpdate(channelName, false, (_, confirmed) => confirmed);
        await TryJoinAsync(channelName, TwitchJoinSource.Command, CancellationToken.None);
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

        _desiredChannels.AddOrUpdate(channelName, false, (_, stored) => stored);
        await TryJoinAsync(channelName, TwitchJoinSource.ConvergenceNet, CancellationToken.None);
    }

    public async Task LeaveChannelAsync(string channelName)
    {
        // Drop the intent first, so nothing rejoins this channel afterwards even if the leave
        // itself fails or the client is currently disconnected. TryJoinAsync re-checks the intent
        // after its gate, so a leave that lands mid-rejoin also stops the JOIN that round would
        // otherwise still send.
        _desiredChannels.TryRemove(channelName, out _);
        // A left channel's last message time would otherwise outlive it for the process lifetime and
        // resurrect as a stale timestamp if the channel is ever rejoined.
        _lastMessageByChannelTicks.TryRemove(channelName, out _);

        if (!IsConnected)
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

    public IReadOnlyList<TwitchRosterEntry> GetRoster()
    {
        // Built from the desired set, not from the message dictionary: a channel we want and never
        // got a single message from is exactly the row worth showing, and it exists only here.
        var roster = new List<TwitchRosterEntry>(_desiredChannels.Count);
        foreach (var (channelName, joinConfirmed) in _desiredChannels)
        {
            var ticks = _lastMessageByChannelTicks.TryGetValue(channelName, out var stored) ? stored : 0;
            roster.Add(new TwitchRosterEntry(
                channelName,
                joinConfirmed,
                ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc)));
        }

        roster.Sort(static (left, right) => string.CompareOrdinal(left.ChannelName, right.ChannelName));
        return roster;
    }

    public void RequestReconnect(TwitchSessionEndReason reason, string detail) =>
        OfferSignal(Volatile.Read(ref _clientGeneration), reason, detail);

    public Task<TwitchReconnectRequest> WaitForReconnectRequestAsync(CancellationToken ct) =>
        _signalSlot.TakeAsync(ct);

    public async Task<TwitchConnectOutcome> ReconnectOnceAsync(CancellationToken ct)
    {
        await _reconnectLock.WaitAsync(ct);
        try
        {
            // Step 1 — detach the old client. From here on nothing it raises reaches us, and the
            // signal slot discards anything it deposited before the loop condemned its generation.
            var oldClient = _client;
            var oldGeneration = Volatile.Read(ref _clientGeneration);
            UnwireClient(oldClient);
            SetConnected(false);
            Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);
            MarkAllChannelsUnconfirmed();

            // Step 2 — clean it up in the background rather than awaiting it (E4). Its socket is
            // already gone, and DisconnectAsync alone carries ~1.9s of built-in waits that would be
            // pure counting gap.
            DisconnectInBackground(oldClient, oldGeneration);

            // Step 3 — a fresh object, never OpenAsync() on the old one: a reused client drags
            // along TwitchLib's half-emptied join queue and its _currentlyJoiningChannels flag,
            // while a new one has an empty queue and exactly one read loop (E3).
            var newClient = CreateClient(loggerFactory);
            WireUpClient(newClient);
            var generation = Interlocked.Increment(ref _clientGeneration);
            _client = newClient;

            // Steps 4 and 5.
            return await OpenAndAwaitHandshakeAsync(newClient, generation, ct);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    public async Task<TwitchRejoinOutcome> RejoinDesiredChannelsAsync(CancellationToken ct)
    {
        var channels = _desiredChannels.Keys.ToArray();
        if (channels.Length == 0)
        {
            return new TwitchRejoinOutcome(0, 0, 0, false);
        }

        logger.LogInformation("Rejoine {Count} gewünschte(n) Channel(s).", channels.Length);

        var aborted = false;
        foreach (var channelName in channels)
        {
            if (ct.IsCancellationRequested || !IsConnected)
            {
                // Abort instead of producing one "aufgeschoben" line per remaining channel: the
                // connection is gone again and its loss is already a signal, or the host is
                // stopping and the remaining channels no longer matter.
                aborted = true;
                break;
            }

            await TryJoinAsync(channelName, TwitchJoinSource.Rejoin, ct);
        }

        if (!aborted)
        {
            aborted = !await WaitForJoinConfirmationsAsync(channels, ct);
        }

        return TallyRejoin(channels, aborted);
    }

    private async Task<TwitchConnectOutcome> OpenAndAwaitHandshakeAsync(
        TwitchClient client,
        int generation,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        Interlocked.Exchange(ref _connectAttemptedUtcTicks, startedAt.Ticks);

        // Created before the connect, because OnConnected can fire the moment the socket is up.
        // RunContinuationsAsynchronously so the loop never resumes inside TwitchLib's read loop.
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshakeSource = handshake;

        bool opened;
        try
        {
            // Exactly one attempt: NoReconnectionPolicy is ReconnectionPolicy(0, maxAttempts: 1),
            // so TwitchLib does not loop here, and its own TimeOutEstablishConnection (15s) bounds
            // how long this can take. No timeout of our own on purpose — that 15s bound is what the
            // "an attempt ends within ~25s" guarantee is measured against, and a second timeout
            // here would hide it rather than test it.
            opened = await client.ConnectAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Abandoned, not awaited: TwitchLib's connect is not cancellable, so the host must not
            // wait for it. The half-open client is disposed of in the background.
            DisconnectInBackground(client, generation);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TwitchClient-Verbindungsaufbau (Client #{Generation}) mit Exception beendet.", generation);
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.ConnectFaulted, DateTime.UtcNow - startedAt, generation);
        }

        if (!opened)
        {
            // ConnectAsync signals failure by returning false rather than throwing — discarding
            // that result used to make a failed connect indistinguishable from a successful one.
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.ConnectFailed, DateTime.UtcNow - startedAt, generation);
        }

        try
        {
            await handshake.Task.WaitAsync(HandshakeTimeout, ct);
        }
        catch (TimeoutException)
        {
            return new TwitchConnectOutcome(false, TwitchSessionEndReason.HandshakeTimeout, DateTime.UtcNow - startedAt, generation);
        }

        return new TwitchConnectOutcome(true, null, DateTime.UtcNow - startedAt, generation);
    }

    private async Task TryJoinAsync(string channelName, TwitchJoinSource source, CancellationToken ct)
    {
        if (!IsConnected)
        {
            // Information, not Warning: with a self-driven rebuild this is an expected state that
            // lasts a few seconds, and the channel stays desired, so the rejoin round or the
            // convergence net picks it up.
            logger.LogInformation(
                "Join für {Channel} aufgeschoben — TwitchClient ist derzeit nicht verbunden.",
                channelName);
            return;
        }

        // Serialises and paces every join we issue, across all callers: boot recovery, the rejoin
        // after a rebuild, Redis join commands and the periodic EnsureJoinedAsync sweep would
        // otherwise each burst independently. A single ad-hoc join is unaffected whenever the
        // previous one is long enough ago.
        try
        {
            await _joinGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var sinceLastJoin = DateTime.UtcNow - _lastJoinIssuedUtc;
            if (sinceLastJoin < MinIntervalBetweenJoins)
            {
                await Task.Delay(MinIntervalBetweenJoins - sinceLastJoin, ct);
            }

            // Re-checked *after* the gate, immediately before sending: a LEAVE that arrives while
            // this join waited its turn would otherwise still be joined, and nobody would ever part
            // it again — the intent it would be parted by is already gone (G5, R9).
            if (!_desiredChannels.ContainsKey(channelName))
            {
                logger.LogDebug(
                    "JOIN für {Channel} verworfen — der Channel ist zwischenzeitlich nicht mehr gewünscht.",
                    channelName);
                return;
            }

            _lastJoinIssuedUtc = DateTime.UtcNow;
            // The origin line: it says *who* wanted this JOIN. TwitchLib's own "Joining channel"
            // line right after it says it was actually sent. Both are needed, because TwitchLib
            // deduplicates a second JOIN for an already-sent channel silently — without this line
            // a confirmation from the convergence net is indistinguishable from one of the rejoin
            // round, and an SLO-2 measurement could be certified by the wrong path.
            logger.LogInformation(
                "JOIN für {Channel} angestoßen (Quelle {Source}, Client #{Generation}).",
                channelName, source, Volatile.Read(ref _clientGeneration));
            await _client.JoinChannelAsync(channelName);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during the throttle pause; the remaining channels are irrelevant now.
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

    /// <summary>
    /// Waits for Twitch to confirm the channels this round sent, and reports whether it got that far
    /// — <c>false</c> means the wait was cut short by the token or by another loss, not that
    /// confirmations are missing (that is <see cref="TwitchRejoinOutcome.Open"/>'s job).
    /// </summary>
    private async Task<bool> WaitForJoinConfirmationsAsync(string[] channels, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + JoinConfirmationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (AllConfirmed(channels))
            {
                return true;
            }

            if (!IsConnected)
            {
                return false;
            }

            try
            {
                await Task.Delay(JoinConfirmationPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    private bool AllConfirmed(string[] channels)
    {
        foreach (var channelName in channels)
        {
            // A channel that left the desired set mid-round is not pending — it is gone.
            if (_desiredChannels.TryGetValue(channelName, out var confirmed) && !confirmed)
            {
                return false;
            }
        }

        return true;
    }

    private TwitchRejoinOutcome TallyRejoin(string[] channels, bool aborted)
    {
        var desired = 0;
        var confirmed = 0;
        foreach (var channelName in channels)
        {
            if (!_desiredChannels.TryGetValue(channelName, out var joinConfirmed))
            {
                // Left mid-round: counted in neither figure, so a deliberate LEAVE can never show
                // up as an unconfirmed channel. The round then closes as "N-1 gewünscht,
                // N-1 bestätigt, 0 offen" rather than accusing itself of a missing join.
                continue;
            }

            desired++;
            if (joinConfirmed)
            {
                confirmed++;
            }
        }

        return new TwitchRejoinOutcome(desired, confirmed, desired - confirmed, aborted);
    }

    public Task SimulateServerReconnectAsync() => _client.OnReadLineTestAsync(":tmi.twitch.tv RECONNECT");

    private void OfferSignal(int generation, TwitchSessionEndReason reason, string detail)
    {
        var request = new TwitchReconnectRequest(reason, detail, CurrentSessionDuration(), DateTime.UtcNow, generation);
        switch (_signalSlot.Offer(request))
        {
            case TwitchReconnectSignalOutcome.Deposited:
                logger.LogInformation(
                    "Verlust-Signal abgelegt ({Reason}): {Detail} — Client #{Generation}.",
                    reason, detail, generation);
                break;
            case TwitchReconnectSignalOutcome.Coalesced:
                logger.LogInformation(
                    "Weiteres Verlust-Signal ({Reason}): {Detail} — mit dem vorliegenden zusammengefasst, Client #{Generation}.",
                    reason, detail, generation);
                break;
            default:
                logger.LogInformation(
                    "Verlust-Signal verworfen ({Reason}): {Detail} — Client #{Generation} ist bereits ersetzt.",
                    reason, detail, generation);
                break;
        }
    }

    /// <summary>
    /// The two-part rule every TwitchLib handler goes through before it may signal: the event must
    /// come from the client we currently hold, and that client must have completed its handshake at
    /// some point — <see cref="_sessionStartedUtcTicks"/> is non-zero exactly then, and only the
    /// start of a rebuild attempt clears it again. The second half matters even with the slot's
    /// generation rule: a fresh attempt's client carries a *higher* generation than the condemned
    /// one, so its own OnDisconnected would otherwise pass as a genuine loss instead of being
    /// reported through the attempt's return value.
    /// </summary>
    private void SignalFromHandler(object? sender, TwitchSessionEndReason reason, string detail)
    {
        if (!ReferenceEquals(sender, _client))
        {
            logger.LogDebug("Ereignis {Reason} eines bereits ersetzten TwitchClient ignoriert.", reason);
            return;
        }

        if (Interlocked.Read(ref _sessionStartedUtcTicks) == 0)
        {
            logger.LogDebug(
                "Ereignis {Reason} eines Clients ohne Handshake ignoriert — der Fehlversuch meldet sich über seinen Rückgabewert.",
                reason);
            return;
        }

        OfferSignal(Volatile.Read(ref _clientGeneration), reason, detail);
    }

    private TimeSpan? CurrentSessionDuration()
    {
        var ticks = Interlocked.Read(ref _sessionStartedUtcTicks);
        return ticks == 0 ? null : DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
    }

    private void DisconnectInBackground(TwitchClient client, int generation) => _ = Task.Run(async () =>
    {
        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            // Never fatal: the replacement is already connected or connecting, and an exception
            // escaping this task would be an unobserved one.
            logger.LogWarning(
                ex,
                "Aufräumen des alten TwitchClient #{Generation} fehlgeschlagen (ignoriert).",
                generation);
        }
    });

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
        client.OnSendReceiveData += OnSendReceiveData;
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
        client.OnSendReceiveData -= OnSendReceiveData;
    }

    private Task OnConnected(object? sender, OnConnectedEventArgs e)
    {
        SetConnected(true);
        Interlocked.Exchange(ref _sessionStartedUtcTicks, DateTime.UtcNow.Ticks);
        logger.LogInformation("TwitchClient verbunden.");

        // No rejoin here (E2). This handler runs synchronously inside TwitchLib's read loop —
        // Handle004 awaits it — so joining 600ms per channel from here blocked the very loop that
        // has to process the join confirmations, and TwitchLib then reported joins as failed whose
        // "366" was already sitting in the socket buffer. The rebuild loop rejoins instead, on its
        // own task, once this handshake is signalled below.
        _handshakeSource?.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnDisconnected(object? sender, OnDisconnectedArgs e)
    {
        // Without a log line here a silent drop (a Twitch-side PING timeout, say) would freeze chat
        // matching for every channel without a trace. Marking the channels unconfirmed lets the
        // rejoin round and EnsureJoinedAsync verify them again.
        SetConnected(false);
        MarkAllChannelsUnconfirmed();
        logger.LogWarning("TwitchClient getrennt.");
        SignalFromHandler(sender, TwitchSessionEndReason.Disconnected, "TwitchLib meldet OnDisconnected.");
        return Task.CompletedTask;
    }

    private Task OnFailureToReceiveJoinConfirmation(object? sender, OnFailureToReceiveJoinConfirmationArgs e)
    {
        // Since the rejoin left the read loop (E2), this line means again what it says: Twitch
        // really did not confirm the join within TwitchLib's 5s window. Before the rebuild, our own
        // throttled rejoin blocked that loop and produced this warning for joins whose confirmation
        // had long arrived. The channel stays in _desiredChannels as unconfirmed, so the round's
        // "K offen" figure counts it and EnsureJoinedAsync retries it.
        logger.LogWarning(
            "Twitch hat den Join für {Channel} nicht bestätigt. Details: {Details}",
            e.Exception.Channel, e.Exception.Details);
        return Task.CompletedTask;
    }

    private Task OnConnectionError(object? sender, OnConnectionErrorArgs e)
    {
        // Information, not Warning: with NoReconnectionPolicy this is the expected follow-up of a
        // loss, not a fault. TwitchLib raises it from RaiseFatal after its (single, already spent)
        // reconnect attempt — about 2s after OnDisconnected, so with a 0s rebuild delay the client
        // is usually unwired before it arrives and it never reaches us at all. It reaches us only
        // while a floor (flap 5s, tripwire 10s) keeps the old client wired, and then the slot's
        // generation rule discards it.
        logger.LogInformation(
            "TwitchClient meldet Verbindungsfehler für {BotUsername}: {Error}",
            e.BotUsername, e.Error.Message);
        SignalFromHandler(sender, TwitchSessionEndReason.ConnectionError, e.Error.Message);
        return Task.CompletedTask;
    }

    private Task OnReconnected(object? sender, OnConnectedEventArgs e)
    {
        // Tripwire. With NoReconnectionPolicy this cannot fire: RaiseReconnected sits behind a
        // successful OpenPrivateAsync(isReconnect: true), whose retry loop runs zero times because
        // Reset(true) does not clear the attempt counter — the call raises OnFatality instead and
        // returns false. If this line ever appears, the library changed underneath us and the
        // client may again be running two read loops on one socket (#114); the policy's 10s floor
        // then keeps our rejoin out of the burst TwitchLib would have sent.
        SetConnected(true);
        logger.LogError(
            "TwitchClient reconnected — das darf mit NoReconnectionPolicy nicht auftreten (Issue #114/#68).");
        SignalFromHandler(
            sender,
            TwitchSessionEndReason.UnexpectedInPlaceReconnect,
            "TwitchLib hat am selben Objekt reconnectet.");
        return Task.CompletedTask;
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

    private Task OnSendReceiveData(object? sender, OnSendReceiveDataArgs e)
    {
        // Any received IRC line proves the socket is alive — including the server PING Twitch sends
        // roughly every five minutes even when every joined channel is silent. This is the liveness
        // signal the watchdog keys off; LastMessageReceivedUtc below stays chat-activity-only.
        // Raised inline from TwitchClient's receive loop for every line, and a synchronous throw
        // here would sever the multicast chain before TwitchLib's own handlers run (verified against
        // TwitchLib.Client 4.0.1, _client_OnMessage) — so this handler must stay allocation-free and
        // exception-free: one branch, one Interlocked store.
        if (e.Direction == SendReceiveDirection.Received)
        {
            Interlocked.Exchange(ref _lastFrameReceivedUtcTicks, DateTime.UtcNow.Ticks);
        }

        return Task.CompletedTask;
    }

    private Task OnMessageReceived(object? sender, OnMessageReceivedArgs e)
    {
        // Aktualisiert für JEDE Nachricht, nicht nur gematchte — der Watchdog erkennt so
        // auch ein stilles Einfrieren der Verbindung auf Channels ohne Emote-Nutzung. This must
        // happen before any room or bot classification below: a mirrored Shared Chat message or a
        // bot message still proves the socket is alive. Moving either check above these two writes
        // would make the watchdog blind to a channel whose only traffic is mirrored-in or from bots
        // and force spurious reconnects — the exact failure mode fixed on 2026-08-03 (see
        // TwitchWatchdogPolicy). Do not reorder.
        var receivedAtTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastMessageReceivedUtcTicks, receivedAtTicks);
        // Hot path: one indexer assignment, no LINQ and no allocation beyond the dictionary's own
        // first insert per channel.
        _lastMessageByChannelTicks[e.ChatMessage.Channel] = receivedAtTicks;

        // Sentinel for the TwitchLib double-read-loop defect (#114): warn and count, never drop —
        // the line still carries a real message and must be classified and matched like any other.
        // Reads RawIrcMessage, not UndocumentedTags (E6, see IrcLineSpliceRule). Hot path: one
        // call, no allocation on the (overwhelming) non-splice branch.
        var rawIrc = e.ChatMessage.RawIrcMessage;
        if (IrcLineSpliceRule.IsSpliced(rawIrc))
        {
            stats.RecordSplicedIrcLine();
            var tagBlock = IrcLineSpliceRule.TagBlockForLog(rawIrc);

            // No message text here on purpose (data minimisation) — the tag block alone is enough
            // to diagnose the splice. That holds for *foreign* text too: TagBlockForLog redacts the
            // value of reply-parent-msg-body, which on a reply carries the parent message verbatim.
            logger.LogWarning(
                "Gespleißte IRC-Zeile erkannt (#114) in Channel {Channel}, RoomId {RoomId}: {TagBlock}",
                e.ChatMessage.Channel, e.ChatMessage.RoomId, tagBlock);
        }

        logger.LogDebug("[{Channel}] {Username}: {Message}",
            e.ChatMessage.Channel, e.ChatMessage.Username, e.ChatMessage.Message);

        var channelEmotes = emoteMatchCache.GetChannelEmotes(e.ChatMessage.Channel);
        if (channelEmotes.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Classified exactly once per message, after the watchdog bookkeeping above (see the
        // class-level comment on that ordering) and before the token loop, so every emote match in
        // this message shares the same category instead of re-classifying the same chatter per
        // token. Room comes before bot (#73, design doc D2/B2): a message mirrored in from a
        // foreign room, or one whose room cannot be determined, is Shared Chat regardless of who
        // sent it. SharedChatRule.FromTags is null-safe — UndocumentedTags is null for the ordinary
        // message that carries no unknown tag at all, not an error case.
        var origin = SharedChatRule.FromTags(e.ChatMessage.RoomId, e.ChatMessage.UndocumentedTags);
        if (origin == MessageOrigin.Indeterminate)
        {
            stats.RecordIndeterminateSharedChatMessage();
        }

        var isBot = botChatterDetector.IsBot(e.ChatMessage.UserId, e.ChatMessage.Badges);
        var category = UsageCategoryRule.Resolve(origin, isBot);

        // Owns the set and iterates it through its concrete type below, so the struct enumerator
        // applies instead of the boxed one behind IReadOnlySet<string> — see the buffer-taking
        // MatchEmoteIds overload's doc comment. Exactly one HashSet<string> allocated per message,
        // same as before this class existed.
        var matchedThisMessage = new HashSet<string>();
        EmoteNameMatching.MatchEmoteIds(e.ChatMessage.Message, channelEmotes, matchedThisMessage);
        foreach (var emoteId in matchedThisMessage)
        {
            usageCounter.Increment(emoteId, category);
        }

        return Task.CompletedTask;
    }

    private void MarkAllChannelsUnconfirmed()
    {
        foreach (var channelName in _desiredChannels.Keys)
        {
            _desiredChannels.TryUpdate(channelName, false, true);
        }
    }

    private void SetConnected(bool value) => Interlocked.Exchange(ref _connected, value ? 1 : 0);

    private static TwitchClient CreateClient(ILoggerFactory loggerFactory) => new(
        client: new WebSocketClient(
            // NoReconnectionPolicy does not mean "never reconnect", it means "exactly one connect
            // attempt per object": verified against TwitchLib.Communication 2.0.1, it is
            // ReconnectionPolicy(reconnectInterval: 0, maxAttempts: 1), and OpenPrivateAsync's
            // Reset(isReconnect: true) returns early *without* clearing _attemptsMade. After the
            // one successful connect the budget is spent, so ReconnectAsync() on such a client can
            // never succeed — its retry loop runs zero times and it raises "Fatal network error."
            // instead. That is deliberate here: every rebuild is a new object (see
            // ReconnectOnceAsync), so the attempt budget is fresh every time and the 2026-07-26
            // trap — a client whose lifetime budget of ten attempts was silently exhausted, leaving
            // the worker offline for >45min — is structurally impossible rather than merely fixed.
            new ClientOptions(new NoReconnectionPolicy()),
            loggerFactory.CreateLogger<WebSocketClient>()),
        loggerFactory: loggerFactory);

    private static DateTime? ReadTimestamp(ref long ticksField)
    {
        var ticks = Interlocked.Read(ref ticksField);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Who wanted a JOIN, for the origin log line. Manager-internal on purpose: it is a measuring
    /// instrument for the rejoin round, not part of the contract with the loop.
    /// </summary>
    private enum TwitchJoinSource
    {
        /// <summary>Boot recovery or a Redis <c>JOIN</c> command.</summary>
        Command,

        /// <summary>The periodic resync or a Redis <c>RESYNC</c> — the convergence net.</summary>
        ConvergenceNet,

        /// <summary>The rejoin round of a rebuild.</summary>
        Rejoin
    }
}
