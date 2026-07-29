using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

public class UsageStatFlushService(AppDbContext db, ILogger<UsageStatFlushService> logger) : IUsageStatFlushService
{
    public async Task FlushAsync(IReadOnlyDictionary<string, int> usageCounts, CancellationToken cancellationToken = default)
    {
        if (usageCounts.Count == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var emoteIds = usageCounts.Keys.ToList();

        // A channel leave deactivates instead of deleting nowadays, but archived emotes can still
        // be hard-deleted by an admin purge — and a count buffered before that would otherwise
        // violate the UsageStat.EmoteId FK and take the whole batch down with it.
        var validIds = await db.Emotes
            .Where(e => emoteIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        if (validIds.Count < emoteIds.Count)
        {
            logger.LogWarning(
                "{Count} gepufferte Usage-Counts für inzwischen gelöschte Emotes verworfen.",
                emoteIds.Count - validIds.Count);
        }

        if (validIds.Count == 0)
        {
            return;
        }

        var useCounts = validIds.Select(id => usageCounts[id]).ToArray();

        // Atomic upsert rather than read-then-insert. The previous version decided per emote between
        // += and Add based on a prior SELECT, which is only correct while there is exactly one
        // writer: the final flush in UsageFlushWorker.StopAsync can overlap a regular 30s tick, and
        // both would then see "row missing" and insert it — one loses on the unique index and its
        // entire batch (up to 30s of chat across ~1.000 emotes) is discarded. This also drops the
        // extra SELECT with its ~1.000-element IN list every 30 seconds.
        const string sql = """
            INSERT INTO "UsageStats" ("EmoteId", "Date", "UseCount")
            SELECT input."EmoteId", @date, input."UseCount"
            FROM UNNEST(@emoteIds, @useCounts) AS input("EmoteId", "UseCount")
            ON CONFLICT ("EmoteId", "Date")
            DO UPDATE SET "UseCount" = "UsageStats"."UseCount" + EXCLUDED."UseCount";
            """;

        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = today },
                new NpgsqlParameter("emoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = validIds.ToArray() },
                new NpgsqlParameter("useCounts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = useCounts },
            ],
            cancellationToken);
    }
}
