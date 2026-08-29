namespace EmotePurge.Core.Twitch;

public interface ITwitchHelixClient
{
    Task<TwitchUserInfo?> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default);

    // Requires the scope "user:read:moderated_channels" on accessToken — the only Helix path that
    // doesn't require the broadcaster to separately authorize this app (see docs/Architectur.md Modul B).
    Task<IReadOnlySet<string>?> GetModeratedChannelLoginsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken = default);

    // Same underlying Helix call as GetModeratedChannelLoginsAsync, but also surfaces each channel's
    // broadcaster id — needed to resolve their 7TV identity without a second, name-based 7TV lookup.
    Task<IReadOnlyList<TwitchModeratedChannelInfo>?> GetModeratedChannelsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken = default);

    // Requires the scope "user:read:subscriptions" on accessToken — self-check, same shape as
    // GetModeratedChannelLoginsAsync. true=subscribed, false=confirmed not subscribed (404),
    // null=transient failure (caller must not cache this outcome).
    Task<bool?> GetUserSubscriptionStatusAsync(string accessToken, string broadcasterTwitchId, string userTwitchId, CancellationToken cancellationToken = default);

    // GET /helix/streams by user_login, batched 100 per request (the Helix cap); needs no scope and
    // runs on an app access token. Returns only the currently live channels — offline means absent
    // from the result, never a row. Null = failure of any batch; the caller must not read that as
    // "everyone is offline".
    Task<IReadOnlyList<TwitchStreamInfo>?> GetLiveStreamsByLoginsAsync(IReadOnlyCollection<string> userLogins, string accessToken, CancellationToken cancellationToken = default);

    // GET /helix/users by id and/or login, batched to 100 parameters *combined* per request — Helix
    // caps id and login together, not 100 of each. Needs no scope and runs on an app access token.
    //
    // Null = some batch failed transiently; the caller must not derive anything from that, and must
    // not cache it. A missing id/login in an otherwise *successful* response means that account
    // does not (or no longer does) exist under that id/login — Helix answers with an empty data
    // array rather than an error for that case. Verified in prod on 2026-08-29:
    // GET /users?login=affeoderwatt returned an empty array, while GET /users?id=955448938 resolved
    // the same account under its new login, affeaufbike. This distinction is exactly what identity
    // reconciliation (rename tracking, id backfill) relies on.
    //
    // Response logins are already lowercase, but the caller normalizes them anyway (Regel 9) rather
    // than relying on that.
    Task<IReadOnlyList<TwitchUserIdentity>?> GetUsersAsync(IReadOnlyCollection<string> ids, IReadOnlyCollection<string> logins, string accessToken, CancellationToken cancellationToken = default);
}
