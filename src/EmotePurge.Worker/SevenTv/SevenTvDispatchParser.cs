using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;

namespace EmotePurge.Worker.SevenTv;

internal static class SevenTvDispatchParser
{
    public static SevenTvEmoteSetDelta ParseEmoteSetUpdate(JsonElement body, ILogger logger)
    {
        var added = new List<SevenTvEmote>();
        var updated = new List<SevenTvEmote>();
        var removedIds = new List<string>();

        if (body.TryGetProperty("added", out var addedArray))
        {
            foreach (var change in addedArray.EnumerateArray())
            {
                TryMapEmoteChange(change, "value", added, logger);
            }
        }

        if (body.TryGetProperty("updated", out var updatedArray))
        {
            foreach (var change in updatedArray.EnumerateArray())
            {
                TryMapEmoteChange(change, "value", updated, logger);
            }
        }

        if (body.TryGetProperty("removed", out var removedArray))
        {
            foreach (var change in removedArray.EnumerateArray())
            {
                TryGetRemovedEmoteId(change, removedIds, logger);
            }
        }

        return new SevenTvEmoteSetDelta(added, updated, removedIds);
    }

    // Only entries with key=="emotes" represent an individual emote change — other keys
    // (e.g. "name") are set-level metadata changes (like a rename of the set itself), not emotes.
    private static bool IsEmoteKey(JsonElement change) =>
        change.TryGetProperty("key", out var key) && key.GetString() == "emotes";

    private static void TryMapEmoteChange(JsonElement change, string valueProperty, List<SevenTvEmote> into, ILogger logger)
    {
        try
        {
            if (!IsEmoteKey(change))
            {
                return;
            }

            if (!change.TryGetProperty(valueProperty, out var value) || value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            into.Add(SevenTvEmoteJsonMapper.MapFromJsonElement(value));
        }
        catch (Exception ex)
        {
            // The exact shape of "updated" entries for individual emote renames hasn't been
            // verified against a live event — never let an unrecognized shape crash the listener.
            logger.LogDebug(ex, "Unbekannte 7TV-Dispatch-Änderung übersprungen.");
        }
    }

    private static void TryGetRemovedEmoteId(JsonElement change, List<string> into, ILogger logger)
    {
        try
        {
            if (!IsEmoteKey(change))
            {
                return;
            }

            if (change.TryGetProperty("old_value", out var oldValue) &&
                oldValue.ValueKind == JsonValueKind.Object &&
                oldValue.TryGetProperty("id", out var id))
            {
                var idValue = id.GetString();
                if (!string.IsNullOrEmpty(idValue))
                {
                    into.Add(idValue);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unbekannte 7TV-Dispatch-Entfernung übersprungen.");
        }
    }
}
