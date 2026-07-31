using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmotePurge.Core.Messaging;

/// <summary>
/// The Worker/Api -> browser notification protocol on a second Redis pub/sub channel, separate from
/// the <see cref="BotCommands"/> command channel: thin events that only say <em>what changed</em>,
/// never the changed data itself. Clients refetch through the existing REST endpoints, which keeps
/// the viewer- and role-specific read models (MyVote, includeRawUsage) behind their own
/// authorization instead of broadcasting them.
/// <para>
/// There is deliberately no version field — the <c>type</c> string is the version. Producers may add
/// a new type at any time, and every consumer silently ignores types it does not know, so Api and
/// Worker can be deployed in either order (same reasoning as the <c>RESYNC:</c> prefix).
/// </para>
/// </summary>
public static class LiveEvents
{
    public const string Channel = "live:events";

    /// <summary>A usage-stat flush committed counts for this channel.</summary>
    public const string UsageFlushed = "usage.flushed";

    /// <summary>A vote was cast or retracted in this channel's vote session. Carries no voter identity.</summary>
    public const string VoteChanged = "vote.changed";

    /// <summary>A 7TV emote-set sync for this channel finished successfully.</summary>
    public const string ChannelSynced = "channel.synced";

    /// <summary>The worker published a fresh health snapshot. No channel scope.</summary>
    public const string WorkerHealth = "worker.health";

    /// <summary>
    /// Heartbeat injected by the Api broker when a stream is idle; never published to Redis.
    /// Consumers drop it — it exists only to keep proxies from timing out an idle connection.
    /// </summary>
    public const string Ping = "ping";

    /// <summary>Types the admin stream (<c>GET /api/admin/live</c>) forwards.</summary>
    public static readonly IReadOnlySet<string> AdminTypes =
        new HashSet<string>(StringComparer.Ordinal) { WorkerHealth, ChannelSynced };

    /// <summary>Types the channel stream (<c>GET /api/channels/{channelName}/live</c>) forwards.</summary>
    public static readonly IReadOnlySet<string> ChannelTypes =
        new HashSet<string>(StringComparer.Ordinal) { UsageFlushed, VoteChanged, ChannelSynced };
}

/// <summary>
/// One live event as it travels over Redis and, byte-identical, as the <c>data:</c> payload of an
/// SSE frame. <see cref="Channel"/> is always a normalized channel name
/// (<see cref="Entities.ChannelName.Normalize"/>) so consumers can compare it without knowing how
/// the producer spelled it; both optional members are omitted from the JSON when null.
/// </summary>
public sealed record LiveEvent(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Channel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? SessionId = null)
{
    /// <summary>The single shared heartbeat instance — it carries no state.</summary>
    public static readonly LiveEvent Heartbeat = new(LiveEvents.Ping);

    /// <summary>
    /// camelCase via <see cref="JsonSerializerOptions.Web"/>, deliberately not via the Api's
    /// configured JSON options: the SSE endpoint writes this string verbatim, so the wire format
    /// cannot drift with an unrelated host-level serializer change.
    /// </summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    /// <summary>
    /// Returns <c>null</c> for anything that is not a live event — malformed JSON, a JSON value that
    /// is not an object, or an object without a <c>type</c>. Callers drop those; a foreign publisher
    /// on the channel must never be able to tear a subscription down.
    /// </summary>
    public static LiveEvent? TryParse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var liveEvent = JsonSerializer.Deserialize<LiveEvent>(payload, JsonSerializerOptions.Web);
            return string.IsNullOrWhiteSpace(liveEvent?.Type) ? null : liveEvent;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
