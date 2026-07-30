namespace EmotePurge.Core.Services;

/// <summary>
/// A user's 7TV editor grants, normalized once. Carries both identifiers because the two callers ask
/// different questions: an authorization check matches on the immutable <see cref="TwitchChannelIds"/>
/// wherever the channel row has one (Twitch releases freed-up logins for re-registration — see the
/// broadcaster check in ChannelAccessService for the same reasoning), while the overview has nothing
/// but logins to work with.
/// </summary>
/// <param name="ChannelLogins">Lower-cased, trimmed Twitch logins of every channel the user may edit.</param>
/// <param name="TwitchChannelIds">The numeric Twitch ids of the same channels.</param>
public record SevenTvEditorGrants(IReadOnlySet<string> ChannelLogins, IReadOnlySet<string> TwitchChannelIds);

/// <summary>
/// Answers "which channels is this user a 7TV editor of?" — the single implementation of a chain
/// (resolve 7TV identity, then look up editor grants) that used to exist twice, with two different
/// string-comparison strategies, in ChannelAccessService and MyChannelsService.
/// </summary>
public interface ISevenTvEditorService
{
    /// <summary>
    /// Returns <c>null</c> when 7TV could not answer (outage, rate limit, unresolvable identity) —
    /// deliberately distinct from an empty set, which means "answered: this user edits nothing".
    /// Callers must not treat the two the same: the overview reports the former as a degradation, and
    /// neither outcome is cached as a negative.
    /// </summary>
    Task<SevenTvEditorGrants?> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default);
}
