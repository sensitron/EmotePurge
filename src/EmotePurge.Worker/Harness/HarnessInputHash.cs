using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// A fingerprint of everything a run compares against: the emote lifetimes, the live usage rows of
/// the window — own, bot and shared-chat counts alike — and the bot account ids. Part of the run
/// identity, and therefore of the report file's name. The shared-chat column moving is as much a
/// reason to start a new run as a changed <c>BotUseCount</c>: it is live data the harness reads and
/// compares against, and a stale hash would let a resume continue against a snapshot Postgres has
/// since moved past.
/// <para>
/// It exists because of the Codex-adversarial finding "JSONL-Kopf identifiziert den Datensnapshot
/// nicht": the live worker keeps writing while the harness runs, so a resume two days later would
/// otherwise silently mix day counts taken against one snapshot of the database with day counts
/// taken against another. A changed emote lifetime, a changed usage row or a changed bot list all
/// move this hash, which moves the file name, which starts a new run instead of continuing a
/// meaningless one.
/// </para>
/// <para>
/// <c>Emote.LastSyncedAt</c> is deliberately **not** part of the hash as a raw timestamp — only as
/// the single boolean <see cref="ReplayFidelityCalculator"/> actually derives from it,
/// <c>LastSyncedAt &lt; windowFrom</c> ("is this emote part of the window's stable subset"). Task-8
/// live verification (#69) found <c>LastSyncedAt</c> moving on essentially every 60-second resync
/// tick for a real channel — 7TV had listed the same emote id twice under two set-entry names
/// (<c>Fiesta</c>/<c>clownFiesta</c>), and <c>SevenTvSyncService.ReconcileAsync</c> upserts the one
/// underlying row once per live entry, re-stamping <c>LastSyncedAt</c> on the second write even
/// though nothing about the row actually changed. Hashing the raw timestamp turned every such tick
/// into a new run identity: four runs in 13 minutes, four report files, the same 8.39 MB pulled
/// from the archive four times — the exact failure the resume promise exists to prevent. Hashing
/// the predicate instead keeps the guarantee precise rather than weaker: a timestamp that moves
/// without crossing the window boundary can never change what the calculation does with it, so it
/// must not change the identity either; a timestamp that crosses the boundary changes which subset
/// the emote belongs to and rightly starts a new run.
/// </para>
/// <para>
/// The canonical form is sorted, so the order the queries happened to return does not matter, and
/// it is textual rather than JSON, so no serializer setting can change it underneath us.
/// </para>
/// </summary>
public static class HarnessInputHash
{
    public static string Compute(
        IReadOnlyList<EmoteLifetimeDto> emotes,
        IReadOnlyList<UsageStatRowDto> rows,
        IReadOnlySet<string> botIds,
        DateOnly windowFrom)
    {
        ArgumentNullException.ThrowIfNull(emotes);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(botIds);

        var canonical = new StringBuilder();

        foreach (var emote in emotes.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            canonical
                .Append("e|").Append(emote.Id)
                .Append('|').Append(emote.Name)
                .Append('|').Append(emote.IsArchived ? '1' : '0')
                .Append('|').Append(Timestamp(emote.FirstSeenAt))
                .Append('|').Append(Timestamp(emote.ArchivedAt))
                .Append('|').Append(DateOnly.FromDateTime(emote.LastSyncedAt) < windowFrom ? '1' : '0')
                .Append('\n');
        }

        foreach (var row in rows.OrderBy(r => r.EmoteId, StringComparer.Ordinal).ThenBy(r => r.Date))
        {
            canonical
                .Append("r|").Append(row.EmoteId)
                .Append('|').Append(row.Date.ToString("o", CultureInfo.InvariantCulture))
                .Append('|').Append(row.UseCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(row.BotUseCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(row.SharedChatUseCount.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        foreach (var botId in botIds.Order(StringComparer.Ordinal))
        {
            canonical.Append("b|").Append(botId).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// ISO 8601 round-trip, empty for an absent timestamp. Round-trip rather than a shorter form on
    /// purpose: it keeps the sub-second part, which is what tells two syncs of the same second
    /// apart.
    /// </summary>
    private static string Timestamp(DateTime? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
}
