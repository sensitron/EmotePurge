namespace EmotePurge.Core.Twitch;

public record TwitchTokenResult(string AccessToken, DateTime ExpiresAtUtc);

public record TwitchUserInfo(string Id, string Login, string DisplayName);

public record TwitchModeratedChannelInfo(string Login, string BroadcasterId);
