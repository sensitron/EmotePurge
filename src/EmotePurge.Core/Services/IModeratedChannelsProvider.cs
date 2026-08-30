using EmotePurge.Core.Twitch;

namespace EmotePurge.Core.Services;

// Channels is null when the moderated-channel list could not be determined right now: no usable
// access token, a transient Helix failure, or a pagination that did not finish. That is explicitly
// different from an empty list, which means the user moderates nothing — callers must not collapse
// the two, because only the empty list is a real answer.
//
// ReauthRequired mirrors ITwitchUserTokenService: true means only a fresh login can help. It is
// always false on a cache hit, because a cache hit deliberately costs no token lookup at all.
public record ModeratedChannelsLookup(IReadOnlyList<TwitchModeratedChannelInfo>? Channels, bool ReauthRequired);

// The single source of a user's full moderated-channel list. Every consumer (overview, moderator
// check, emote-set ownership) goes through here instead of paginating Helix itself, so a burst of
// requests from one user costs one Helix pagination per TTL rather than one per request.
public interface IModeratedChannelsProvider
{
    Task<ModeratedChannelsLookup> GetModeratedChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}
