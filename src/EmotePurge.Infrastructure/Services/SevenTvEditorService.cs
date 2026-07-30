using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvEditorService(
    ISevenTvApiClient sevenTvApiClient,
    IModRoleCache modRoleCache,
    ILogger<SevenTvEditorService> logger) : ISevenTvEditorService
{
    public async Task<SevenTvEditorGrants?> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var cached = await modRoleCache.TryGetSevenTvEditorGrantsAsync(twitchUserId, cancellationToken);
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
        // both — and a test for one said nothing about the other.
        var grants = new SevenTvEditorGrants(
            new HashSet<string>(editorOf.Select(grant => ChannelName.Normalize(grant.TwitchChannelLogin)), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(editorOf.Select(grant => grant.TwitchChannelId), StringComparer.Ordinal));

        await modRoleCache.SetSevenTvEditorGrantsAsync(twitchUserId, grants, cancellationToken);
        return grants;
    }
}
