namespace EmotePurge.Core.SevenTv;

public interface ISevenTvApiClient
{
    // Never null: the outcome is the answer. Ok carries the resolved Twitch user id, the three
    // failure statuses say why there is none — a distinction that used to be lost in a bare null.
    Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default);

    // The channel's active emote set plus the 7TV account id behind the Twitch connection — both
    // come from the same users/twitch/{id} response, so resolving them together costs no extra call.
    // Never null; State is populated if and only if Status is Ok. The three failure statuses are the
    // three ways this call can legitimately produce nothing, and they must stay apart: only one of
    // them ("no active emote set") is something the channel owner can fix.
    Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Resolves a Twitch user's own 7TV account identity plus their currently active Twitch-linked
    // emote set, via 7TV's userByConnection GQL query.
    Task<SevenTvIdentity?> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // The actual owner of a given 7TV emote set — distinct from whichever channel currently has it
    // active, since 7TV lets an editor point a channel's active set at someone else's set entirely.
    Task<string?> GetEmoteSetOwnerIdAsync(string emoteSetId, CancellationToken cancellationToken = default);

    // Channels (by their Twitch connection) that the given 7TV account holds editor rights on.
    Task<IReadOnlyList<SevenTvEditorGrant>?> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default);
}
