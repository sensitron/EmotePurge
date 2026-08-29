namespace EmotePurge.Core.Services;

/// <summary>
/// One 7TV editor grant, exactly as 7TV paired the two identifiers when the grant was resolved.
/// </summary>
/// <param name="ChannelLogin">Normalized via <see cref="Entities.ChannelName.Normalize"/> — this is
/// 7TV's own copy of the login, which can be stale after a Twitch rename.</param>
/// <param name="TwitchChannelId">The numeric Twitch id, opaque and compared with
/// <see cref="StringComparison.Ordinal"/> — never normalized.</param>
public record SevenTvEditorGrantEntry(string ChannelLogin, string TwitchChannelId);

/// <summary>
/// A user's 7TV editor grants, normalized once. Carries both identifiers because the two callers ask
/// different questions: an authorization check matches on the immutable <see cref="TwitchChannelIds"/>
/// wherever the channel row has one (Twitch releases freed-up logins for re-registration — see the
/// broadcaster check in ChannelAccessService for the same reasoning), while the overview has nothing
/// but logins to work with.
/// </summary>
/// <param name="ChannelLogins">Lower-cased, trimmed Twitch logins of every channel the user may edit.</param>
/// <param name="TwitchChannelIds">The numeric Twitch ids of the same channels.</param>
/// <param name="Entries">
/// The same grants as login↔id pairs. <see cref="ChannelLogins"/> and <see cref="TwitchChannelIds"/>
/// exist for authorization (<c>ChannelAccessService</c>), which only ever asks "is this login/id in
/// the grant set" — deliberately kept independent of a second foreign system, since the auth path
/// must not depend on Helix being reachable. <see cref="Entries"/> exists for the overview
/// (<c>MyChannelsService</c>), which has to decide per grant whether 7TV's reported login is still
/// current or needs resolving against Helix. Omitting this parameter yields an empty list, which is
/// also what a cache entry written before this field existed deserializes to — see
/// <c>ModRoleCache</c> for how that legacy shape is produced and <c>MyChannelsService</c> for how it
/// is detected (empty <see cref="Entries"/> alongside a non-empty <see cref="ChannelLogins"/>) and
/// handled.
/// </param>
public record SevenTvEditorGrants(IReadOnlySet<string> ChannelLogins, IReadOnlySet<string> TwitchChannelIds, IReadOnlyList<SevenTvEditorGrantEntry> Entries)
{
    public SevenTvEditorGrants(IReadOnlySet<string> channelLogins, IReadOnlySet<string> twitchChannelIds)
        : this(channelLogins, twitchChannelIds, [])
    {
    }
}

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
