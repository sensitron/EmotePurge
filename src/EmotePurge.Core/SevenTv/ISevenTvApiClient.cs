namespace EmotePurge.Core.SevenTv;

public interface ISevenTvApiClient
{
    Task<string?> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default);

    Task<SevenTvEmoteSet?> GetEmoteSetForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Resolves a Twitch user's own 7TV account identity plus their currently active Twitch-linked
    // emote set, via 7TV's userByConnection GQL query.
    Task<SevenTvIdentity?> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // The actual owner of a given 7TV emote set — distinct from whichever channel currently has it
    // active, since 7TV lets an editor point a channel's active set at someone else's set entirely.
    Task<string?> GetEmoteSetOwnerIdAsync(string emoteSetId, CancellationToken cancellationToken = default);

    // Channels (by their Twitch connection) that the given 7TV account holds editor rights on.
    Task<IReadOnlyList<SevenTvEditorGrant>?> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default);
}
