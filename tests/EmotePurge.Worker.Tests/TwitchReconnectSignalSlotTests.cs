using EmotePurge.Worker;
using Xunit;

namespace EmotePurge.Worker.Tests;

public class TwitchReconnectSignalSlotTests
{
    private static readonly DateTime RequestedUtc = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

    private static TwitchReconnectRequest Request(
        int generation,
        TwitchSessionEndReason reason = TwitchSessionEndReason.Disconnected,
        string detail = "TwitchClient getrennt") =>
        new(reason, detail, SessionDuration: null, RequestedUtc, generation);

    [Fact]
    public async Task Offer_OnEmptySlot_DepositsAndTakeReturnsThatSignalAndEmptiesTheSlot()
    {
        var slot = new TwitchReconnectSignalSlot();
        var request = Request(generation: 1);

        var outcome = slot.Offer(request);

        Assert.Equal(TwitchReconnectSignalOutcome.Deposited, outcome);

        var taken = await slot.TakeAsync(CancellationToken.None);

        Assert.Equal(request, taken);

        // The slot is empty again: offering a fresh request deposits rather than coalesces.
        var afterTake = slot.Offer(Request(generation: 2));
        Assert.Equal(TwitchReconnectSignalOutcome.Deposited, afterTake);
    }

    [Fact]
    public async Task Offer_OnFullSlot_ReplacesOnlyReasonAndDetail()
    {
        var slot = new TwitchReconnectSignalSlot();
        var first = Request(generation: 1, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt");
        slot.Offer(first);

        var second = Request(generation: 1, TwitchSessionEndReason.ConnectionError, "Fatal network error.");
        var outcome = slot.Offer(second);

        Assert.Equal(TwitchReconnectSignalOutcome.Coalesced, outcome);

        var taken = await slot.TakeAsync(CancellationToken.None);

        Assert.Equal(second.Reason, taken.Reason);
        Assert.Equal(second.Detail, taken.Detail);
        Assert.Equal(first.RequestedUtc, taken.RequestedUtc);
        Assert.Equal(first.SessionDuration, taken.SessionDuration);
        Assert.Equal(first.ClientGeneration, taken.ClientGeneration);
    }

    [Fact]
    public async Task TakeAsync_SignalOfferedBeforeTheWait_IsDeliveredImmediately()
    {
        // The boot case: the initial ConnectAsync fails and offers before the watchdog loop ever
        // starts waiting.
        var slot = new TwitchReconnectSignalSlot();
        var request = Request(generation: 1, TwitchSessionEndReason.InitialConnectFailed, "Boot-Connect fehlgeschlagen");
        slot.Offer(request);

        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(request, taken);
    }

    [Fact]
    public async Task TakeAsync_WakesAWaitingTaker()
    {
        var slot = new TwitchReconnectSignalSlot();

        // SemaphoreSlim.WaitAsync registers the waiter synchronously before returning an incomplete
        // task, so by the time this line has executed the wait is already in place — no fixed delay
        // needed to make the subsequent Offer race-free.
        var takeTask = slot.TakeAsync(CancellationToken.None);

        var request = Request(generation: 1);
        slot.Offer(request);

        var taken = await takeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(request, taken);
    }

    [Fact]
    public async Task TakeAsync_CancellationEndsTheWaitWithoutASignal()
    {
        var slot = new TwitchReconnectSignalSlot();
        using var cts = new CancellationTokenSource();

        var takeTask = slot.TakeAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => takeTask);
    }

    [Fact]
    public async Task Offer_ForACondemnedGeneration_IsDiscardedAndTheNextWaitRunsIntoItsDeadline()
    {
        // The PG1 sequence: a signal is taken (its generation condemned), then a late echo of the
        // same, already-replaced client (its OnConnectionError) must not refill the slot.
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 1, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt"));
        await slot.TakeAsync(CancellationToken.None);

        var outcome = slot.Offer(Request(generation: 1, TwitchSessionEndReason.ConnectionError, "Fatal network error."));

        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, outcome);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slot.TakeAsync(cts.Token));
    }

    [Fact]
    public async Task Offer_AfterACondemnedGeneration_ANewerGenerationIsStillDeposited()
    {
        // The rule only discards the past: a genuinely new client (a new connection) still gets
        // through.
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 1));
        await slot.TakeAsync(CancellationToken.None);

        var request = Request(generation: 2, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt");
        var outcome = slot.Offer(request);

        Assert.Equal(TwitchReconnectSignalOutcome.Deposited, outcome);

        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(request, taken);
    }

    [Fact]
    public async Task CondemnedGeneration_GrowsMonotonically()
    {
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 1));
        await slot.TakeAsync(CancellationToken.None);
        slot.Offer(Request(generation: 3));
        await slot.TakeAsync(CancellationToken.None);

        // Generation 2 is older than the highest generation taken (3) even though it was never
        // taken itself — the condemned watermark never moves backward.
        var outcome = slot.Offer(Request(generation: 2));

        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, outcome);
    }

    [Fact]
    public async Task Offer_OnFullSlotWithACondemnedGeneration_IsDiscardedNotCoalesced()
    {
        // Order of checks: generation before slot occupancy.
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 1));
        await slot.TakeAsync(CancellationToken.None);

        // Fill the slot with a newer, un-condemned generation.
        slot.Offer(Request(generation: 2, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt"));

        // A late signal for the already-condemned generation 1 arrives while the slot is full.
        var outcome = slot.Offer(Request(generation: 1, TwitchSessionEndReason.ConnectionError, "Fatal network error."));

        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, outcome);

        // The slot still holds only the generation-2 signal, untouched by the discarded offer.
        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, taken.ClientGeneration);
        Assert.Equal("TwitchClient getrennt", taken.Detail);
    }

    [Fact]
    public void CondemnGeneration_DiscardsALaterOfferForThatGeneration()
    {
        // The failed attempt: its signal was never taken, so only retiring its client condemns it.
        // What it offers afterwards — a late OnDisconnected during the backoff — must not land.
        var slot = new TwitchReconnectSignalSlot();

        slot.CondemnGeneration(2);

        var outcome = slot.Offer(Request(generation: 2, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt"));

        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, outcome);
    }

    [Fact]
    public async Task CondemnGeneration_RemovesASignalAlreadyLyingInTheSlot()
    {
        // Astra's sequence, steps 4 to 6: the failed attempt's client drops during the backoff and
        // its signal is deposited *before* the next attempt retires it. Left in the slot, the loop
        // would find it after the successful rebuild and tear the healthy connection down again —
        // so retiring the generation has to remove it, and the next wait must run into its
        // deadline rather than hand out a dead signal.
        var slot = new TwitchReconnectSignalSlot();
        Assert.Equal(
            TwitchReconnectSignalOutcome.Deposited,
            slot.Offer(Request(generation: 2, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt")));

        slot.CondemnGeneration(2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slot.TakeAsync(cts.Token));
    }

    [Fact]
    public async Task CondemnGeneration_AfterRemovingASignal_TheSlotStillAcceptsTheNextOne()
    {
        // The wake-up token has to be taken back together with the signal: the slot's semaphore
        // holds at most one, so a token left behind would make this Offer throw instead of deposit
        // — and the rebuild after the *next* loss would never be requested.
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 2, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt"));
        slot.CondemnGeneration(2);

        var request = Request(generation: 3, TwitchSessionEndReason.ConnectionError, "Fatal network error.");
        var outcome = slot.Offer(request);

        Assert.Equal(TwitchReconnectSignalOutcome.Deposited, outcome);

        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(request, taken);
    }

    [Fact]
    public async Task CondemnGeneration_LeavesAHigherGenerationsSignalUntouched()
    {
        // Retiring generation 2 must not swallow the loss of the client that replaced it — the
        // rule condemns the past, never the present.
        var slot = new TwitchReconnectSignalSlot();
        var request = Request(generation: 3, TwitchSessionEndReason.Disconnected, "TwitchClient getrennt");
        slot.Offer(request);

        slot.CondemnGeneration(2);

        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(request, taken);
    }

    [Fact]
    public async Task CondemnGeneration_GrowsTheWatermarkMonotonically()
    {
        // Same watermark as taking uses, and it never moves backward: retiring an older generation
        // after a newer one has been condemned may not re-admit anything in between.
        var slot = new TwitchReconnectSignalSlot();
        slot.CondemnGeneration(5);
        slot.CondemnGeneration(2);

        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, slot.Offer(Request(generation: 3)));
        Assert.Equal(TwitchReconnectSignalOutcome.Discarded, slot.Offer(Request(generation: 5)));
        Assert.Equal(TwitchReconnectSignalOutcome.Deposited, slot.Offer(Request(generation: 6)));

        var taken = await slot.TakeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, taken.ClientGeneration);
    }

    [Fact]
    public async Task CondemnGeneration_AfterATakeOfALaterGeneration_DoesNotResurrectAnything()
    {
        // The ordinary path stays intact when both condemning points meet: the loop takes the
        // signal of generation 4 (condemning 4), the rebuild then retires generation 4 as well —
        // the second condemnation is a no-op and the empty slot stays empty.
        var slot = new TwitchReconnectSignalSlot();
        slot.Offer(Request(generation: 4));
        await slot.TakeAsync(CancellationToken.None);

        slot.CondemnGeneration(4);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slot.TakeAsync(cts.Token));
    }
}
