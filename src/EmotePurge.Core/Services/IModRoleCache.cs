namespace EmotePurge.Core.Services;

// Short-lived cache for the live role checks that back authorization decisions. Every entry is a
// deliberate staleness trade-off: a positive result outlives a /unmod and a negative one outlives a
// /mod, both by up to the TTL. Accepted (see decision log) because the alternative is an uncached
// third-party call on every single request — and those quotas are per application, not per user.
//
// The moderated-channel answer is no longer stored here: it lives as one whole list per user in
// IModeratedChannelsProvider, because a bool per user AND channel paid a full Helix pagination
// again for every further channel the same user asked about. Only its invalidation stayed here,
// so the admin escape hatch keeps clearing every role input in one call.
public interface IModRoleCache
{
    // 7TV editor grants. The uncached path costs two sequential 7TV GraphQL calls (identity resolve,
    // then editor-of lookup) purely to answer an authorization question. Cached as the whole grant set
    // per user rather than one bool per user+channel: the underlying 7TV call returns all of them
    // anyway, so a per-channel cache paid the same two calls again for the user's second channel, and
    // the overview (which needs the full list) could not use it at all.
    Task<SevenTvEditorGrants?> TryGetSevenTvEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default);

    Task SetSevenTvEditorGrantsAsync(string twitchUserId, SevenTvEditorGrants grants, CancellationToken cancellationToken = default);

    // Twitch subscription status, keyed by broadcaster id rather than channel name. Callers must not
    // cache an unknown (null) result: Helix returning nothing means "we could not tell", and storing
    // that as "not subscribed" would lock a legitimate subscriber out for the whole TTL.
    Task<bool?> TryGetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, CancellationToken cancellationToken = default);

    Task SetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, bool isSubscriber, CancellationToken cancellationToken = default);

    // Drops every cached role answer for one user — the shared moderated-channel list, the
    // subscriber checks and the 7TV editor grants alike — so the next check resolves live. This is
    // the admin escape hatch for the staleness trade-off described above (a /unmod -> /mod flip
    // used to require deleting the Redis key by hand). Returns the number of entries actually
    // removed.
    Task<int> InvalidateUserAsync(string twitchUserId, CancellationToken cancellationToken = default);
}
