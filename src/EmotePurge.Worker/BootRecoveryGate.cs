namespace EmotePurge.Worker;

// Worker-internal handshake between Worker's boot recovery and SevenTvPeriodicResyncWorker.
// Both call ISevenTvSyncService.SyncChannelAsync; running them concurrently for the same channel
// makes two AppDbContext instances insert the same (ChannelId, SevenTvEmoteId) rows and one of
// them loses on the unique index. The first sync of a large channel (~1.000 emotes) is slow
// enough to reach into the resync worker's 60s tick, so the overlap is not hypothetical.
//
// Deliberately a plain singleton class rather than an interface: it holds no external dependency
// and will never be swapped for another implementation.
public sealed class BootRecoveryGate
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completed => _completed.Task;

    // Called from a finally block: boot recovery failing must not deadlock the resync worker.
    public void MarkCompleted() => _completed.TrySetResult();
}
