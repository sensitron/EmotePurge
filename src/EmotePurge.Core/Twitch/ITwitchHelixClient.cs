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
}
