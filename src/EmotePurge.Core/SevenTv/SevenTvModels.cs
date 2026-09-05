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

/// <summary>
/// What a full channel sync resolved. Callers use the set id for logging and the pair to keep an
/// EventAPI subscription registry converged after every sync. <see cref="HasChanges"/> reports
/// whether the sync actually altered the channel's emote inventory (added/archived/unarchived/renamed
/// emote, or a switched active set) — the unattended sync paths publish their <c>channel.synced</c>
/// live event only then, so a no-op resync stays silent.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChannelName"/> is the reason this type exists as more than a tuple. The sync re-reads
/// its row under the row gate, so the login it finishes on is not necessarily the one the caller
/// passed in: a rename committed while the call sat queued behind another sync of the same row
/// retires the old login mid-flight. Before issue #60 only the service learned the new name, and the
/// worker callers went on keying their two after-effects — the EventAPI subscription registry and
/// the <c>channel.synced</c> publish — on the name they had handed in. Both then addressed a login
/// nobody listens on any more, and the registry entry could even resurrect a subscription the
/// handover's LEAVE had just torn down.
/// </para>
/// <para>
/// A sealed class with a private constructor rather than a record, per the decision-log entry of
/// 2026-09-04: the positional constructor and <c>with</c> would leave every caller free to build a
/// result whose <see cref="ChannelName"/> is the stale one again, which is precisely the value this
/// type now exists to carry correctly.
/// </para>
/// </remarks>
public sealed class SevenTvSyncResult
{
    private SevenTvSyncResult(string channelName, string emoteSetId, string? sevenTvUserId, bool hasChanges)
    {
        ChannelName = channelName;
        EmoteSetId = emoteSetId;
        SevenTvUserId = sevenTvUserId;
        HasChanges = hasChanges;
    }

    /// <summary>
    /// The channel's normalized login <b>as of the row read under the row gate</b> — not necessarily
    /// the one the caller passed to <c>SyncChannelAsync</c>. Anything a caller keys on the channel
    /// after the sync belongs on this value.
    /// </summary>
    public string ChannelName { get; }

    public string EmoteSetId { get; }

    public string? SevenTvUserId { get; }

    public bool HasChanges { get; }

    /// <summary>
    /// Builds the result of a completed sync.
    /// <para>
    /// <paramref name="channelName"/> comes from our own <c>Channel</c> row — normalized on the way
    /// in and unique-indexed, so blank is not a shape the database can hold.
    /// <paramref name="emoteSetId"/> can be blank in 7TV's answer (its DTO defaults the field to an
    /// empty string), and this guard is only safe because <c>SevenTvSyncService</c> rejects that
    /// answer <em>before</em> it writes anything. The order matters and is not cosmetic: this
    /// factory runs after the row has been saved and the match cache refreshed, so a throw here
    /// would leave that work committed while the caller treats the sync as failed and skips the
    /// subscription convergence and the <c>channel.synced</c> publish. A guard that late is only
    /// allowed to state an impossibility, never to be the one catching a real case.
    /// </para>
    /// </summary>
    public static SevenTvSyncResult Create(string channelName, string emoteSetId, string? sevenTvUserId, bool hasChanges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoteSetId);
        return new SevenTvSyncResult(channelName, emoteSetId, sevenTvUserId, hasChanges);
    }
}

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

/// <summary>
/// One applied delta plus the login the row actually carried while it was applied (issue #60) — see
/// <see cref="SevenTvSyncResult.ChannelName"/> for why the caller's own name is not good enough.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChannelName"/> is non-null if and only if the row was read under the row gate, and the
/// two factories encode which outcomes that is. <see cref="SevenTvDeltaOutcome.Applied"/>,
/// <see cref="SevenTvDeltaOutcome.SetNotActive"/> and
/// <see cref="SevenTvDeltaOutcome.ImplausibleSkipped"/> are only reachable after that read, so they
/// always carry a name; <see cref="SevenTvDeltaOutcome.ChannelUnknown"/> means there is no row to
/// take a name from and never carries one.
/// </para>
/// <para>
/// <see cref="SevenTvDeltaOutcome.NoChange"/> is the one outcome on both sides, and deliberately so:
/// an empty delta short-circuits before any database access (no name to give), while a delta that
/// turned out to be a no-op against the stored rows has been through the gate (name known). Callers
/// must not read that difference as meaningful — <c>NoChange</c> means "nothing was written" in both
/// cases, and neither publishes anything.
/// </para>
/// </remarks>
public sealed class SevenTvDeltaResult
{
    private SevenTvDeltaResult(SevenTvDeltaOutcome outcome, string? channelName)
    {
        Outcome = outcome;
        ChannelName = channelName;
    }

    public SevenTvDeltaOutcome Outcome { get; }

    /// <summary>
    /// The channel's normalized login as of the row read under the row gate; null when the call
    /// ended before that read. See the remarks on this type for which outcomes carry one.
    /// </summary>
    public string? ChannelName { get; }

    /// <summary>Builds a result for a delta that got as far as reading its row under the row gate.</summary>
    public static SevenTvDeltaResult ForChannel(SevenTvDeltaOutcome outcome, string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ThrowIfUndefined(outcome);
        if (outcome == SevenTvDeltaOutcome.ChannelUnknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "ChannelUnknown heißt, dass keine Zeile gefunden wurde — dann gibt es auch keinen Login zu melden. SevenTvDeltaResult.WithoutChannel(outcome) ist dafür zuständig.");
        }

        return new SevenTvDeltaResult(outcome, channelName);
    }

    /// <summary>
    /// Builds a result for a delta that ended before the row was read — an empty delta, or a channel
    /// that is not in Postgres (any more).
    /// </summary>
    public static SevenTvDeltaResult WithoutChannel(SevenTvDeltaOutcome outcome)
    {
        ThrowIfUndefined(outcome);
        if (outcome is SevenTvDeltaOutcome.Applied or SevenTvDeltaOutcome.SetNotActive or SevenTvDeltaOutcome.ImplausibleSkipped)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                $"{outcome} ist erst erreichbar, nachdem die Zeile unter dem Zeilen-Gate gelesen wurde — der Login ist dort bekannt und gehört mitgegeben. SevenTvDeltaResult.ForChannel(outcome, channelName) ist dafür zuständig.");
        }

        return new SevenTvDeltaResult(outcome, null);
    }

    private static void ThrowIfUndefined(SevenTvDeltaOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome, "Unbekanntes SevenTvDeltaOutcome.");
        }
    }
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

/// <summary>
/// Rejects a <see cref="SevenTvLookupStatus"/> that no <c>Failed(...)</c> factory of this family may
/// carry. Shared by all four result types below and by
/// <see cref="EmotePurge.Core.Services.SevenTvEditorGrantsLookupResult"/>, so the guard exists once instead of five
/// times.
/// </summary>
/// <remarks>
/// <para>
/// Two rejections, for two different mistakes. <see cref="SevenTvLookupStatus.Ok"/> is the success
/// status: a caller that hands it to a failure factory is asking for the one value this whole family
/// exists to make impossible — a "successful" result with no payload — and failing loudly at the
/// source beats a <see cref="NullReferenceException"/> in whichever service dereferences the
/// payload behind an <c>Ok</c> check. An undefined cast like <c>(SevenTvLookupStatus)9</c> is
/// rejected for the mirror reason: it matches no arm of any caller's switch, so it would travel as
/// far as the default arm of something that never expected to have one.
/// </para>
/// <para>
/// A success status added to <see cref="SevenTvLookupStatus"/> later has to be added here — and get
/// its own success factory on every type of the family — in the same commit that adds it to the
/// enum.
/// </para>
/// </remarks>
internal static class SevenTvLookupStatusGuard
{
    internal static void ThrowIfNotAFailure(SevenTvLookupStatus status, string typeName, string successFactory)
    {
        if (status == SevenTvLookupStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                $"{typeName}.Failed() kann keinen Erfolgsstatus tragen — für Ok ist {typeName}.{successFactory} zuständig.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Unbekannter SevenTvLookupStatus.");
        }
    }
}

/// <summary>
/// The channel state plus why it is absent. <see cref="State"/> is non-null if and only if
/// <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>, and the two factories are the only
/// way to build one at all — so that invariant cannot be broken at a call site, not even by accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this family is sealed classes and not records</b> — written once here for all five types
/// (the four in this file and <see cref="EmotePurge.Core.Services.SevenTvEditorGrantsLookupResult"/>), which share
/// one shape: a <see cref="SevenTvLookupStatus"/> plus a payload that only <c>Ok</c> carries.
/// </para>
/// <para>
/// A record cannot keep the promise its own doc comment makes. Its positional constructor is public
/// and <c>with</c> sits on top of that, so <c>new SevenTvChannelStateResult(SevenTvLookupStatus.Ok,
/// null)</c> and <c>result with { State = null }</c> stay open however carefully the factories are
/// written — and an <c>Ok</c> without a payload is exactly the value every caller of these types
/// assumes cannot exist, because every one of them dereferences the payload behind an <c>Ok</c>
/// check. Adding factories alone only adds a safe path; it does not remove the unsafe ones. That is
/// the lesson of issue #55, where the first attempt copied this very family's shape and reproduced
/// its gap instead of closing it — see the decision-log entry of 2026-09-04 and
/// <see cref="EmotePurge.Core.Services.ChannelJoinResult"/>, which was closed first.
/// </para>
/// <para>
/// Losing value equality costs nothing here: no call site compares two of these, and for the two
/// types whose payload is a list or a mutable object, record equality would have looked like a
/// guarantee it never gave.
/// </para>
/// </remarks>
public sealed class SevenTvChannelStateResult
{
    private SevenTvChannelStateResult(SevenTvLookupStatus status, SevenTvChannelState? state)
    {
        Status = status;
        State = state;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>.</summary>
    public SevenTvChannelState? State { get; }

    public static SevenTvChannelStateResult Ok(SevenTvChannelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SevenTvChannelStateResult(SevenTvLookupStatus.Ok, state);
    }

    public static SevenTvChannelStateResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvChannelStateResult), "Ok(state)");
        return new SevenTvChannelStateResult(status, null);
    }
}

/// <summary>
/// Same shape for the Twitch-id resolution, which can only ever end in <c>Ok</c>,
/// <see cref="SevenTvLookupStatus.NoSevenTvAccount"/> (no 7TV user carries that Twitch connection) or
/// <see cref="SevenTvLookupStatus.Unavailable"/>. A separate type rather than a generic envelope: the
/// property name says what it holds, which a <c>Value</c> never would. Built like
/// <see cref="SevenTvChannelStateResult"/> and for the same reason.
/// </summary>
public sealed class SevenTvTwitchUserIdResult
{
    private SevenTvTwitchUserIdResult(SevenTvLookupStatus status, string? twitchUserId)
    {
        Status = status;
        TwitchUserId = twitchUserId;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>.</summary>
    public string? TwitchUserId { get; }

    public static SevenTvTwitchUserIdResult Ok(string twitchUserId)
    {
        ArgumentNullException.ThrowIfNull(twitchUserId);
        return new SevenTvTwitchUserIdResult(SevenTvLookupStatus.Ok, twitchUserId);
    }

    public static SevenTvTwitchUserIdResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvTwitchUserIdResult), "Ok(twitchUserId)");
        return new SevenTvTwitchUserIdResult(status, null);
    }
}

/// <summary>
/// Same shape again for the 7TV identity resolution (issue #37): it used to collapse "no 7TV account
/// linked to this Twitch id" and "7TV unreachable" onto the same null, which made
/// <c>MyChannelsService</c> show the "7TV editor status could not be checked" warning to users who
/// simply have no 7TV account at all. Only <c>Ok</c> and
/// <see cref="SevenTvLookupStatus.NoSevenTvAccount"/>/<see cref="SevenTvLookupStatus.Unavailable"/>
/// are reachable in practice — there is no active-emote-set concept at the identity level. Built like
/// <see cref="SevenTvChannelStateResult"/> and for the same reason.
/// </summary>
public sealed class SevenTvIdentityResult
{
    private SevenTvIdentityResult(SevenTvLookupStatus status, SevenTvIdentity? identity)
    {
        Status = status;
        Identity = identity;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>.</summary>
    public SevenTvIdentity? Identity { get; }

    public static SevenTvIdentityResult Ok(SevenTvIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SevenTvIdentityResult(SevenTvLookupStatus.Ok, identity);
    }

    public static SevenTvIdentityResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvIdentityResult), "Ok(identity)");
        return new SevenTvIdentityResult(status, null);
    }
}

/// <summary>
/// Same shape for the editor_of lookup (issue #37): the grant list defaults to an empty collection on
/// its own DTO, so a genuinely empty grant set already deserializes as <c>Ok</c> with an empty list —
/// this <see cref="Status"/> only ever turns <see cref="SevenTvLookupStatus.Unavailable"/> when the
/// response itself is unusable (GraphQL error, or the queried 7TV user id — already resolved as valid
/// earlier in the same call chain — coming back empty). Built like
/// <see cref="SevenTvChannelStateResult"/> and for the same reason.
/// </summary>
public sealed class SevenTvEditorGrantsResult
{
    private SevenTvEditorGrantsResult(SevenTvLookupStatus status, IReadOnlyList<SevenTvEditorGrant>? grants)
    {
        Status = status;
        Grants = grants;
    }

    public SevenTvLookupStatus Status { get; }

    /// <summary>
    /// Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLookupStatus.Ok"/>. An empty
    /// list is a legitimate <c>Ok</c> — "answered: this user edits nothing" is not a failure.
    /// </summary>
    public IReadOnlyList<SevenTvEditorGrant>? Grants { get; }

    public static SevenTvEditorGrantsResult Ok(IReadOnlyList<SevenTvEditorGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return new SevenTvEditorGrantsResult(SevenTvLookupStatus.Ok, grants);
    }

    public static SevenTvEditorGrantsResult Failed(SevenTvLookupStatus status)
    {
        SevenTvLookupStatusGuard.ThrowIfNotAFailure(status, nameof(SevenTvEditorGrantsResult), "Ok(grants)");
        return new SevenTvEditorGrantsResult(status, null);
    }
}
