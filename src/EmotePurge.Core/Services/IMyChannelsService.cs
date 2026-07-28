namespace EmotePurge.Core.Services;

// Independent flags, not a single role — a channel can be broadcaster-self, Twitch-moderator,
// 7TV-editor, any combination, or (7TV-editor-only) none of the Twitch roles at all.
public record MyChannelDto(
    string ChannelName,
    bool IsBroadcaster,
    bool IsModerator,
    bool IsSevenTvEditor,
    bool IsTracked,
    bool IsBotActive);

// HelixUnavailable=true means the moderated-channels part of the list couldn't be refreshed
// (expired/missing access token, or a transient Helix failure) — Channels then only contains
// the broadcaster-self entry, not a full picture; the frontend should say so, not treat it as "no channels".
// SevenTvUnavailable is the same idea for the 7TV editor_of lookup.
public record MyChannelsResultDto(bool HelixUnavailable, bool SevenTvUnavailable, IReadOnlyList<MyChannelDto> Channels);

public interface IMyChannelsService
{
    Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}
