using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvEditorService(
    ISevenTvApiClient sevenTvApiClient,
    IModRoleCache modRoleCache,
    IRateLimitTelemetry telemetry,
    ILogger<SevenTvEditorService> logger) : ISevenTvEditorService
{
    public async Task<SevenTvEditorGrants?> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var cached = await modRoleCache.TryGetSevenTvEditorGrantsAsync(twitchUserId, cancellationToken);
        // A miss here costs two 7TV REST calls (identity, then grants), which is what makes this hit
        // rate worth watching at all.
        telemetry.RecordCacheLookup(RateLimitCacheNames.SevenTvGrants, cached is not null);
        if (cached is not null)
        {
            return cached;
        }

        var identity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(twitchUserId, cancellationToken);
        if (identity is null)
        {
            // Not cached, and not reported as "edits nothing": a 7TV outage means "unknown", and
            // storing it as a negative would lock genuine editors out for the whole TTL.
            logger.LogInformation("7TV-Identität für {User} nicht auflösbar — Editor-Grants unbekannt.", twitchUserId);
            return null;
        }

        var editorOf = await sevenTvApiClient.GetEditorOfChannelsAsync(identity.SevenTvUserId, cancellationToken);
        if (editorOf is null)
        {
            logger.LogInformation("7TV-Editor-Grants für {User} nicht abrufbar.", twitchUserId);
            return null;
        }

        // The one place where grant logins get normalized. Previously done twice with two different
        // comparison strategies (OrdinalIgnoreCase in the access check, ToLowerInvariant dictionary
        // keys in the overview), so a change to 7TV's grant semantics had to be followed correctly in
        // both — and a test for one said nothing about the other. Entries is built first and the two
        // sets are derived from it, so there is exactly one projection over editorOf, not three.
        var entries = editorOf
            .Select(grant => new SevenTvEditorGrantEntry(ChannelName.Normalize(grant.TwitchChannelLogin), grant.TwitchChannelId))
            .ToList();
        var grants = new SevenTvEditorGrants(
            new HashSet<string>(entries.Select(entry => entry.ChannelLogin), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(entries.Select(entry => entry.TwitchChannelId), StringComparer.Ordinal),
            entries);

        await modRoleCache.SetSevenTvEditorGrantsAsync(twitchUserId, grants, cancellationToken);
        return grants;
    }
}
