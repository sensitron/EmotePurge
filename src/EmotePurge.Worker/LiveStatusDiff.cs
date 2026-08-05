namespace EmotePurge.Worker;

/// <summary>
/// Pure diff between two consecutive live polls — the entire decision behind the
/// <c>live.changed</c> event, kept TwitchLib- and Redis-free next to the other worker policies.
/// A null baseline means "no previous statement" (first poll ever, or the Redis snapshot had
/// expired before boot) and yields no changes: better to miss one transition than to storm every
/// open tab with events for channels that merely kept their state.
/// </summary>
public static class LiveStatusDiff
{
    public static LiveStatusChanges Compute(
        IReadOnlySet<string>? previousLiveLogins,
        IReadOnlyCollection<string> currentLiveLogins)
    {
        if (previousLiveLogins is null)
        {
            return LiveStatusChanges.None;
        }

        var current = currentLiveLogins as IReadOnlySet<string>
            ?? currentLiveLogins.ToHashSet(StringComparer.Ordinal);
        var wentLive = current.Where(login => !previousLiveLogins.Contains(login)).ToList();
        var wentOffline = previousLiveLogins.Where(login => !current.Contains(login)).ToList();
        return new LiveStatusChanges(wentLive, wentOffline);
    }
}

public sealed record LiveStatusChanges(
    IReadOnlyList<string> WentLive,
    IReadOnlyList<string> WentOffline)
{
    public static readonly LiveStatusChanges None = new([], []);

    public bool IsEmpty => WentLive.Count == 0 && WentOffline.Count == 0;
}
