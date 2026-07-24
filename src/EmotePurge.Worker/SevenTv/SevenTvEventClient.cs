using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.SevenTv;

public class SevenTvEventClient(ILogger<SevenTvEventClient> logger, IServiceScopeFactory scopeFactory)
    : ISevenTvEventClient, IAsyncDisposable
{
    private static readonly Uri EventApiUri = new("wss://events.7tv.io/v3");
    private const int DefaultHeartbeatIntervalMs = 45000;

    // channelName -> emoteSetId. Channel-Leave löscht die Postgres-Zeile bereits, bevor der
    // Worker das LEAVE-Kommando verarbeitet (s. ChannelService.LeaveAsync) — ohne dieses
    // In-Memory-Mapping wüsste UnsubscribeAsync nicht mehr, welches Set es verlassen soll.
    private readonly ConcurrentDictionary<string, string> _activeSubscriptions = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetimeCts;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunConnectionLoopAsync(_lifetimeCts.Token);
        return Task.CompletedTask;
    }

    public async Task SubscribeAsync(string channelName, string emoteSetId, CancellationToken cancellationToken = default)
    {
        _activeSubscriptions[Normalize(channelName)] = emoteSetId;
        await SendFrameAsync(35, emoteSetId, cancellationToken);
    }

    public async Task UnsubscribeAsync(string channelName, CancellationToken cancellationToken = default)
    {
        if (_activeSubscriptions.TryRemove(Normalize(channelName), out var emoteSetId))
        {
            await SendFrameAsync(36, emoteSetId, cancellationToken);
            logger.LogInformation("7TV-Set {SetId} für {Channel} unsubscribed.", emoteSetId, channelName);
        }
    }

    private static string Normalize(string channelName) => channelName.Trim().ToLowerInvariant();

    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var firstConnect = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!firstConnect)
                {
                    await ResubscribeAndResyncAsync(ct);
                }
                firstConnect = false;

                await ConnectOnceAndPumpAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "7TV EventAPI Verbindung verloren, reconnect in 5s.");
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectOnceAndPumpAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(EventApiUri, ct);
        _socket = socket;
        logger.LogInformation("7TV EventAPI verbunden.");

        var heartbeatTimeoutMs = DefaultHeartbeatIntervalMs * 3;
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(heartbeatTimeoutMs);

                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeoutCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        logger.LogInformation("7TV EventAPI hat die Verbindung geschlossen.");
                        return;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                heartbeatTimeoutMs = await HandleMessageAsync(json, heartbeatTimeoutMs, ct);
            }
        }
        finally
        {
            _socket = null;
        }
    }

    private async Task<int> HandleMessageAsync(string json, int currentHeartbeatTimeoutMs, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetInt32();

        switch (op)
        {
            case 0: // Dispatch
                await HandleDispatchAsync(root, ct);
                break;

            case 1: // Hello
                if (root.TryGetProperty("d", out var helloData) &&
                    helloData.TryGetProperty("heartbeat_interval", out var interval))
                {
                    currentHeartbeatTimeoutMs = interval.GetInt32() * 3;
                }
                logger.LogInformation("7TV EventAPI Hello empfangen.");
                break;

            case 2: // Heartbeat
                break;

            default:
                logger.LogDebug("Unbehandelter 7TV-Opcode {Op} ignoriert.", op);
                break;
        }

        return currentHeartbeatTimeoutMs;
    }

    private async Task HandleDispatchAsync(JsonElement root, CancellationToken ct)
    {
        try
        {
            var d = root.GetProperty("d");
            if (!d.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "emote_set.update")
            {
                return;
            }

            var body = d.GetProperty("body");
            var emoteSetId = body.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(emoteSetId))
            {
                return;
            }

            var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(body, logger);

            using var scope = scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
            await syncService.ApplyEmoteSetUpdateAsync(emoteSetId, delta, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "7TV-Dispatch-Verarbeitung fehlgeschlagen, übersprungen.");
        }
    }

    private async Task ResubscribeAndResyncAsync(CancellationToken ct)
    {
        foreach (var (channelName, emoteSetId) in _activeSubscriptions)
        {
            await SendFrameAsync(35, emoteSetId, ct);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
                var currentEmoteSetId = await syncService.SyncChannelAsync(channelName, ct);

                if (currentEmoteSetId is not null && currentEmoteSetId != emoteSetId)
                {
                    // Streamer hat das aktive 7TV-Set während der Downtime gewechselt.
                    await SendFrameAsync(36, emoteSetId, ct);
                    await SendFrameAsync(35, currentEmoteSetId, ct);
                    _activeSubscriptions[channelName] = currentEmoteSetId;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Post-Reconnect-Resync für {Channel} fehlgeschlagen.", channelName);
            }
        }
    }

    private async Task SendFrameAsync(int op, string emoteSetId, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            logger.LogDebug("7TV-Socket nicht offen, Frame (op {Op}) wird beim nächsten Reconnect nachgeholt.", op);
            return;
        }

        var payload = new SubscribePayload(op, new SubscribeBody("emote_set.update", new SubscribeCondition(emoteSetId)));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Senden des 7TV-Frames (op {Op}) fehlgeschlagen.", op);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts?.Cancel();

        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
            catch
            {
                // Best-effort beim Shutdown.
            }
        }
    }

    private sealed record SubscribePayload(
        [property: JsonPropertyName("op")] int Op,
        [property: JsonPropertyName("d")] SubscribeBody D);

    private sealed record SubscribeBody(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("condition")] SubscribeCondition Condition);

    private sealed record SubscribeCondition(
        [property: JsonPropertyName("object_id")] string ObjectId);
}
