namespace EmotePurge.Core.Twitch;

public interface ITwitchHelixClient
{
    Task<TwitchUserInfo?> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default);

    // Requires the scope "user:read:moderated_channels" on accessToken — the only Helix path that
    // doesn't require the broadcaster to separately authorize this app (see Architectur.md Modul B).
    Task<IReadOnlySet<string>?> GetModeratedChannelLoginsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken = default);
}
