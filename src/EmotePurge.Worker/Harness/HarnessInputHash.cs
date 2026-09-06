using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// A fingerprint of everything a run compares against: the emote lifetimes, the live usage rows of
/// the window and the bot account ids. Part of the run identity, and therefore of the report file's
/// name.
/// <para>
/// It exists because of the Codex-adversarial finding "JSONL-Kopf identifiziert den Datensnapshot
/// nicht": the live worker keeps writing while the harness runs, so a resume two days later would
/// otherwise silently mix day counts taken against one snapshot of the database with day counts
/// taken against another. A changed emote lifetime, a changed usage row or a changed bot list all
/// move this hash, which moves the file name, which starts a new run instead of continuing a
/// meaningless one.
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
        IReadOnlySet<string> botIds)
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
                .Append('|').Append(Timestamp(emote.LastSyncedAt))
                .Append('\n');
        }

        foreach (var row in rows.OrderBy(r => r.EmoteId, StringComparer.Ordinal).ThenBy(r => r.Date))
        {
            canonical
                .Append("r|").Append(row.EmoteId)
                .Append('|').Append(row.Date.ToString("o", CultureInfo.InvariantCulture))
                .Append('|').Append(row.UseCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(row.BotUseCount.ToString(CultureInfo.InvariantCulture))
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
