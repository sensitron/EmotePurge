namespace EmotePurge.Core.SevenTv;

public interface ISevenTvApiClient
{
    Task<string?> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default);

    Task<SevenTvEmoteSet?> GetEmoteSetForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default);
}
