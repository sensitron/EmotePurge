namespace EmotePurge.Core.SevenTv;

// AddedToSetAt is when this emote entered the set. Resolved from the v4 GQL schema
// (EmoteSetEmote.addedAt) — the v3 payload's own timestamp field carries the emote's *upload*
// date and is deliberately not mapped. Null means unresolved: the EventAPI dispatch path only
// knows the date for pushes (where "now" is the truth), so "unknown" must stay distinguishable
// from "just added".
public record SevenTvEmote(string Id, string Name, string ImageUrl, DateTime? AddedToSetAt = null);

// Capacity is the set's slot limit as 7TV reports it, null when the response omits it or reports 0.
// Never assume 1000: 7TV subscribers get larger sets, so the number has to travel with the set
// instead of being hard-coded anywhere downstream.
public record SevenTvEmoteSet(string Id, IReadOnlyList<SevenTvEmote> Emotes, int? Capacity = null);

// The channel's currently active set plus the 7TV account behind the Twitch connection, resolved
// together from one users/twitch/{id} REST call. The account id is what the EventAPI's user.*
// subscription needs to detect active-set switches; it is distinct from the set's owner, which can
// be a third party (see GetEmoteSetOwnerIdAsync).
public record SevenTvChannelState(string? SevenTvUserId, SevenTvEmoteSet EmoteSet);

// What a full channel sync resolved. Callers use the set id for logging and the pair to keep an
// EventAPI subscription registry converged after every sync. HasChanges reports whether the sync
// actually altered the channel's emote inventory (added/archived/unarchived/renamed emote, or a
// switched active set) — the unattended sync paths publish their channel.synced live event only
// then, so a no-op resync stays silent.
public record SevenTvSyncResult(string EmoteSetId, string? SevenTvUserId, bool HasChanges);

// One emote_set.update dispatch from the EventAPI, reduced to the three change kinds the wire
// actually carries. Property names deliberately mirror the wire fields (pushed/pulled/updated):
// the 2026-07 implementation silently produced empty deltas because it read added/removed —
// field names that do not exist in real dispatches (docs/Untersuchung-7TV-WebSocket-2026-07-30.md).
public record SevenTvEmoteSetDelta(
    IReadOnlyList<SevenTvEmote> Pushed,
    IReadOnlyList<SevenTvEmote> Updated,
    IReadOnlyList<string> PulledIds)
{
    public bool IsEmpty => Pushed.Count == 0 && Updated.Count == 0 && PulledIds.Count == 0;
}

// A user.update dispatch that switched the active emote set of a Twitch connection. Old id is
// null when the wire omits it; new id is null when the connection ends up with no active set.
public record SevenTvUserSetChange(string SevenTvUserId, string? OldEmoteSetId, string? NewEmoteSetId);

// Result of applying one EventAPI delta to one channel. SetNotActive and ImplausibleSkipped tell
// the caller to fall back to a full resync — which it must run outside the delta call, because
// SyncChannelAsync takes the same non-reentrant per-channel gate.
public enum SevenTvDeltaOutcome
{
    Applied,
    NoChange,
    ChannelUnknown,
    SetNotActive,
    ImplausibleSkipped
}

// A 7TV account's own identity plus its currently active Twitch-linked emote set, resolved together
// in one GQL call (userByConnection) — reused both to find "is this my own set" (owner comparison)
// and, per moderated channel, "what's currently active there" (shared-set detection).
public record SevenTvIdentity(string SevenTvUserId, string? ActiveEmoteSetId);

// One entry in a 7TV user's editor_of list, reduced to the Twitch identity of the channel they can
// edit — the 7TV-internal user id of the owner isn't needed by any current consumer.
public record SevenTvEditorGrant(string TwitchChannelLogin, string TwitchChannelId);

// Why a 7TV lookup produced no usable answer. Four outcomes used to collapse onto one `null`
// (issue #32): "no 7TV account", "account but no active emote set", "7TV unreachable" and "never
// synced" were indistinguishable, and the second one did not even log. Ok is not a failure and
// never reaches the wire — SevenTvSyncFailureReasons maps the other three onto the API contract.
public enum SevenTvLookupStatus
{
    Ok,
    NoSevenTvAccount,
    NoActiveEmoteSet,
    Unavailable
}

// The channel state plus why it is absent. State is non-null if and only if Status is Ok; the two
// factories are the only supported way to build one, so that invariant cannot be broken at a call
// site.
public record SevenTvChannelStateResult(SevenTvLookupStatus Status, SevenTvChannelState? State)
{
    public static SevenTvChannelStateResult Ok(SevenTvChannelState state) =>
        new(SevenTvLookupStatus.Ok, state);

    public static SevenTvChannelStateResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}

// Same shape for the Twitch-id resolution, which can only ever end in Ok, NoSevenTvAccount (no 7TV
// user carries that Twitch connection) or Unavailable. A separate record rather than a generic
// envelope: the property name says what it holds, which a `Value` never would.
public record SevenTvTwitchUserIdResult(SevenTvLookupStatus Status, string? TwitchUserId)
{
    public static SevenTvTwitchUserIdResult Ok(string twitchUserId) =>
        new(SevenTvLookupStatus.Ok, twitchUserId);

    public static SevenTvTwitchUserIdResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}

// Same shape again for the 7TV identity resolution (issue #37): it used to collapse "no 7TV account
// linked to this Twitch id" and "7TV unreachable" onto the same null, which made
// MyChannelsService show the "7TV editor status could not be checked" warning to users who simply
// have no 7TV account at all. Only Ok and NoSevenTvAccount/Unavailable are reachable in practice —
// there is no active-emote-set concept at the identity level.
public record SevenTvIdentityResult(SevenTvLookupStatus Status, SevenTvIdentity? Identity)
{
    public static SevenTvIdentityResult Ok(SevenTvIdentity identity) =>
        new(SevenTvLookupStatus.Ok, identity);

    public static SevenTvIdentityResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}

// Same shape for the editor_of lookup (issue #37): the grant list defaults to an empty collection on
// its own DTO, so a genuinely empty grant set already deserializes as Ok with an empty list — this
// Status only ever turns Unavailable when the response itself is unusable (GraphQL error, or the
// queried 7TV user id — already resolved as valid earlier in the same call chain — coming back
// empty).
public record SevenTvEditorGrantsResult(SevenTvLookupStatus Status, IReadOnlyList<SevenTvEditorGrant>? Grants)
{
    public static SevenTvEditorGrantsResult Ok(IReadOnlyList<SevenTvEditorGrant> grants) =>
        new(SevenTvLookupStatus.Ok, grants);

    public static SevenTvEditorGrantsResult Failed(SevenTvLookupStatus status) =>
        new(status, null);
}
