namespace EmotePurge.Core.Services;

// Independent flags, not a single role — a channel can be broadcaster-self, Twitch-moderator,
// 7TV-editor, any combination, or (7TV-editor-only) none of the Twitch roles at all.
// LiveState is one of ChannelLiveStates; "unknown" covers both a missing snapshot and a channel
// the worker never polled (bot inactive) — the frontend renders it as no statement, not as offline.
public record MyChannelDto(
    string ChannelName,
    bool IsBroadcaster,
    bool IsModerator,
    bool IsSevenTvEditor,
    bool IsTracked,
    bool IsBotActive,
    string LiveState = ChannelLiveStates.Unknown);

// HelixUnavailable=true means the moderated-channels part of the list couldn't be refreshed
// (no usable access token even after a refresh attempt, or a transient Helix failure) — Channels
// then only contains the broadcaster-self entry, not a full picture; the frontend should say so,
// not treat it as "no channels". ReauthRequired sharpens that state: the token store says only a
// fresh Twitch login can fix it (refresh token revoked/absent/scope drift), so the frontend should
// offer a re-login instead of a generic "Helix is down" note. SevenTvUnavailable is the same idea
// as HelixUnavailable for the 7TV editor_of lookup. LivePolledAtUtc is when the worker's live poll
// behind the per-channel LiveState values ran — null when no snapshot exists, so the frontend can
// say "as of x minutes ago" without a second request.
public record MyChannelsResultDto(
    bool HelixUnavailable,
    bool ReauthRequired,
    bool SevenTvUnavailable,
    IReadOnlyList<MyChannelDto> Channels,
    DateTime? LivePolledAtUtc = null);

public interface IMyChannelsService
{
    Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}
