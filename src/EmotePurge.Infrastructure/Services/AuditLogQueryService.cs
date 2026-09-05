using System.Text.Json;

using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AuditLogQueryService(AppDbContext db) : IAuditLogQueryService
{
    /// <summary>
    /// Cap for <see cref="AuditLogDetail.Text"/>. Vote-session titles are user input and are only
    /// bounded by their own validation; a log row is not the place to render an essay.
    /// </summary>
    private const int MaxDetailTextLength = 200;

    // Property names in emotes.syncImported's DetailsJson payload. Deliberately not in
    // AuditLogDetail.Kinds: "sourceKind" is only the discriminator that picks between the two import
    // Kinds, never a Kind itself, and "sourceChannelName" feeds the Text of whichever is chosen.
    private const string SourceKindProperty = "sourceKind";
    private const string SourceChannelNameProperty = "sourceChannelName";

    public async Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, AuditLogFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var query = ApplyFilter(db.AuditLogEntries.AsNoTracking(), filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(e => e.OccurredAtUtc)
            // Id descending as the tiebreaker, not decoration: entries written inside one transaction
            // share a timestamp to the tick, and without a total order Skip/Take may return the same
            // row on two pages and drop another entirely.
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.OccurredAtUtc,
                e.ActorLogin,
                e.Action,
                e.ChannelName,
                e.TargetType,
                e.TargetId,
                e.DetailsJson,
            })
            .ToListAsync(cancellationToken);

        // Projected after materializing, never inside the Select: ProjectDetail parses JSON, which
        // EF Core cannot translate — putting it in the query would throw at runtime, not at build.
        var items = rows
            .Select(r => new AuditLogEntryDto(
                r.Id,
                r.OccurredAtUtc,
                r.ActorLogin,
                r.Action,
                r.ChannelName,
                r.TargetType,
                r.TargetId,
                ProjectDetail(r.DetailsJson)))
            .ToList();

        return new PagedResult<AuditLogEntryDto>(items, page, pageSize, totalCount);
    }

    private static IQueryable<AuditLogEntry> ApplyFilter(IQueryable<AuditLogEntry> query, AuditLogFilter? filter)
    {
        if (filter is null)
        {
            return query;
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.ChannelName))
        {
            // Exact match on the normalized form (Regel 9) — this is what the
            // (ChannelName, OccurredAtUtc) index serves, unlike a substring scan.
            var normalized = ChannelName.Normalize(filter.ChannelName);
            query = query.Where(e => e.ChannelName == normalized);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActorLogin))
        {
            // Substring on purpose: logins are free text to the admin. ILIKE cannot use the
            // btree index, but the actor-filtered set is small enough that this is fine.
            var pattern = $"%{filter.ActorLogin.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.ActorLogin, pattern));
        }

        return query;
    }

    /// <summary>
    /// Reduces a free-form <c>DetailsJson</c> payload to the closed set of
    /// <see cref="AuditLogDetail.Kinds"/>. Every step degrades to <c>null</c> rather than throwing:
    /// the column is written by ten different call sites and read by two endpoints, so one malformed
    /// row must cost its own detail line and nothing else.
    /// <para>
    /// The precedence between kinds is fixed, because a payload could carry more than one known key.
    /// <c>login</c> is recognized and deliberately dropped: user-scoped actions already carry the
    /// target's login in their details, and rendering it would produce "by sensitron · handofblood"
    /// with nothing saying which of the two names is the target.
    /// </para>
    /// </summary>
    private static AuditLogDetail? ProjectDetail(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Checked ahead of the bare EmoteCount case below, and that order is load-bearing: an
        // emotes.syncImported payload carries emoteCount too, and if EmoteCount matched first it
        // would win the precedence and silently drop the one thing that row can't be reconstructed
        // from otherwise — where the emotes came from (R1 in the #71 import plan). Degrades to the
        // branches below (rather than returning null outright) when sourceKind is present but
        // unrecognized, or when emoteCount itself is missing or non-numeric.
        if (root.TryGetProperty(SourceKindProperty, out var sourceKindElement)
            && sourceKindElement.ValueKind == JsonValueKind.String)
        {
            var isKnownSourceKind = sourceKindElement.GetString() is "channel" or "file";

            if (isKnownSourceKind && TryReadCount(root, AuditLogDetail.Kinds.EmoteCount, out var importedCount))
            {
                string? source = null;
                if (root.TryGetProperty(SourceChannelNameProperty, out var sourceChannelNameElement)
                    && sourceChannelNameElement.ValueKind == JsonValueKind.String
                    && sourceChannelNameElement.GetString() is { Length: > 0 } sourceChannelName)
                {
                    source = sourceChannelName.Length > MaxDetailTextLength
                        ? sourceChannelName[..MaxDetailTextLength]
                        : sourceChannelName;
                }

                // The name decides the Kind, not sourceKind alone: the channel variant's translation
                // interpolates the source, so a row claiming a channel origin without one would
                // render "N emotes from ". The endpoint already rejects that combination; this keeps
                // the reader honest for anything that got in before it did, or around it.
                return source is null
                    ? new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromFile, importedCount, null)
                    : new AuditLogDetail(AuditLogDetail.Kinds.ImportedFromChannel, importedCount, source);
            }
        }

        if (TryReadCount(root, AuditLogDetail.Kinds.EmoteCount, out var emoteCount))
        {
            return new AuditLogDetail(AuditLogDetail.Kinds.EmoteCount, emoteCount, null);
        }

        if (TryReadCount(root, AuditLogDetail.Kinds.RemovedEntries, out var removedEntries))
        {
            return new AuditLogDetail(AuditLogDetail.Kinds.RemovedEntries, removedEntries, null);
        }

        if (root.TryGetProperty(AuditLogDetail.Kinds.Title, out var title)
            && title.ValueKind == JsonValueKind.String
            && title.GetString() is { Length: > 0 } text)
        {
            return new AuditLogDetail(
                AuditLogDetail.Kinds.Title,
                null,
                text.Length > MaxDetailTextLength ? text[..MaxDetailTextLength] : text);
        }

        return null;
    }

    private static bool TryReadCount(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }
}
