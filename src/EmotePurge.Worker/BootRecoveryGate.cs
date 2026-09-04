namespace EmotePurge.Worker;

// Worker-internal handshake between Worker's boot phase and the hosted services that must not run
// before it. Two independent signals, because two different things are being waited for.
//
// Completed — boot recovery is over. Both call ISevenTvSyncService.SyncChannelAsync; running them
// concurrently for the same channel makes two AppDbContext instances insert the same
// (ChannelId, SevenTvEmoteId) rows and one of them loses on the unique index. The first sync of a
// large channel (~1.000 emotes) is slow enough to reach into the resync worker's 60s tick, so the
// overlap is not hypothetical.
//
// CommandChannelSubscribed — the worker is listening on BotCommands.Channel. Redis Pub/Sub has no
// store-and-forward: PublishAsync to a channel nobody is subscribed to reports zero receivers and
// discards the message. Anything that *publishes a command the worker itself has to act on* must
// therefore wait for this, not just for Completed (issue #54). Completed alone is not enough:
// Worker subscribes after boot recovery, so there is a real window in which boot recovery is done
// and the command channel is still deaf.
//
// Deliberately a plain singleton class rather than an interface: it holds no external dependency
// and will never be swapped for another implementation.
public sealed class BootRecoveryGate
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _commandChannelSubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completed => _completed.Task;

    public Task CommandChannelSubscribed => _commandChannelSubscribed.Task;

    // Called from a finally block: boot recovery failing must not deadlock the resync worker.
    public void MarkCompleted() => _completed.TrySetResult();

    // Deliberately *not* in a finally: a failed subscribe means the worker will never receive a
    // command, and letting publishers proceed on that assumption would be worse than making them
    // wait for the host to come down (an escaping ExecuteAsync exception stops it —
    // BackgroundServiceExceptionBehavior defaults to StopHost).
    public void MarkCommandChannelSubscribed() => _commandChannelSubscribed.TrySetResult();
}
