using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The only server -> browser push channel of the app: <c>text/event-stream</c> over the existing
/// cookie session, so every endpoint filter and the whole auth pipeline apply unchanged (a SignalR
/// hub would have needed its own). Both streams share <see cref="OpenAsync"/>; they differ only in
/// which events they let through.
/// </summary>
public static class LiveEndpoints
{
    /// <summary>
    /// Upper bound on one connection's lifetime. Session revocation only runs at the handshake
    /// (OnValidatePrincipal), so an open stream would otherwise outlive a revoked session for as
    /// long as the browser kept it. The browser's automatic EventSource reconnect goes through the
    /// full auth pipeline again, so this costs nothing but bounds the revocation window at 10 min.
    /// </summary>
    private static readonly TimeSpan MaxConnectionLifetime = TimeSpan.FromMinutes(10);

    public static void MapLiveEndpoints(this WebApplication app)
    {
        // Deliberately no rate-limiting policy on either stream. Both existing policies are fixed
        // windows with QueueLimit = 0, and an Api restart makes every open browser reconnect at
        // once — a 429 there is final for an EventSource, which stops retrying. The connection
        // limits inside ILiveEventStream are the protection instead.
        app.MapGet("/api/channels/{channelName}/live", (
            string channelName,
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            CancellationToken ct) =>
        {
            // Only "logged in and a well-formed name" — no usage-stats or vote filter. The events
            // are pure "something changed" pings carrying no channel data; the real authorization
            // boundary sits on the refetch each client then performs.
            var normalized = ChannelName.Normalize(channelName);
            return OpenAsync(
                httpContext,
                liveEventStream,
                liveEvent => LiveEvents.ChannelTypes.Contains(liveEvent.Type)
                    && string.Equals(liveEvent.Channel, normalized, StringComparison.Ordinal),
                ct);
        })
        .RequireAuthorization()
        .AddEndpointFilter<ChannelNameValidationFilter>();

        // The overview's stream: any logged-in user may listen. Cross-channel on purpose — the
        // events are rare (a real transition of a tracked channel) and carry only the channel
        // name; the refetch through GET /api/channels/mine stays the authorization boundary.
        // The competing route is GET /api/channels/{channelName} — same segment count, so routing
        // decides by precedence, and a literal segment always outranks a parameter.
        app.MapGet("/api/channels/live-events", (
            HttpContext httpContext,
            ILiveEventStream liveEventStream,
            CancellationToken ct) =>
            OpenAsync(
                httpContext,
                liveEventStream,
                liveEvent => string.Equals(liveEvent.Type, LiveEvents.LiveChanged, StringComparison.Ordinal),
                ct))
        .RequireAuthorization();
    }

    /// <summary>
    /// The admin stream. Registered from <see cref="AdminEndpoints"/> so it sits inside the
    /// <c>/api/admin</c> group and inherits its GlobalAdminAuthorizationFilter by construction.
    /// </summary>
    internal static Task<IResult> OpenAdminAsync(
        HttpContext httpContext,
        ILiveEventStream liveEventStream,
        CancellationToken ct)
        => OpenAsync(httpContext, liveEventStream, liveEvent => LiveEvents.AdminTypes.Contains(liveEvent.Type), ct);

    private static async Task<IResult> OpenAsync(
        HttpContext httpContext,
        ILiveEventStream liveEventStream,
        Func<LiveEvent, bool> filter,
        CancellationToken ct)
    {
        // Per login, not per connection: the limit is meant to bound how many streams one account
        // can pin, and every one of these endpoints requires authentication.
        var subscriberKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

        var subscription = await liveEventStream.SubscribeAsync(subscriberKey, filter, ct);
        if (subscription is null)
        {
            // Before a single byte of the body: once an SSE response has started there is no status
            // code left to send, which is why SubscribeAsync answers eagerly instead of failing
            // lazily during enumeration.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Deliberately in OnStarting and not set directly here: ServerSentEventsResult writes its own
        // Cache-Control ("no-cache,no-store") while executing, so anything assigned before returning
        // the result is silently overwritten — verified against the running server, not assumed.
        // OnStarting runs when the response actually starts, i.e. last.
        httpContext.Response.OnStarting(() =>
        {
            var headers = httpContext.Response.Headers;
            // no-transform on top of no-cache: a transforming proxy is free to buffer and recode a
            // response body, which for an event stream means it arrives in blocks or not at all.
            headers.CacheControl = "no-cache, no-transform";
            // nginx buffers proxied responses by default and would hold frames back until its buffer
            // fills; this is the documented opt-out.
            headers["X-Accel-Buffering"] = "no";
            return Task.CompletedTask;
        });

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        lifetime.CancelAfter(MaxConnectionLifetime);

        return TypedResults.ServerSentEvents(StreamAsync(subscription, lifetime, lifetime.Token));
    }

    /// <summary>
    /// Frames the events. The SSE event type stays the default (<c>message</c>) on purpose: the
    /// discriminator lives inside the JSON body, so a client's <c>onmessage</c> sees every event and
    /// can ignore types it does not know — with a named event type an unknown type would instead be
    /// silently undeliverable.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> StreamAsync(
        ILiveEventSubscription subscription,
        CancellationTokenSource lifetime,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await using (subscription)
            {
                await foreach (var liveEvent in subscription.Events.WithCancellation(ct))
                {
                    // SseItem<string> rather than SseItem<LiveEvent>: strings are written verbatim,
                    // so the bytes on the wire are exactly LiveEvent.Serialize() and cannot drift
                    // with the host's JSON options.
                    yield return new SseItem<string>(liveEvent.Serialize());
                }
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
