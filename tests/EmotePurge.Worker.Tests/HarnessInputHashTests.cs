using EmotePurge.Core.Services;
using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The Codex-adversarial finding "JSONL-Kopf identifiziert den Datensnapshot nicht": a resume is
// only allowed to continue a file whose head describes the same data snapshot. This hash is that
// description, so it has to be blind to ordering and sensitive to every field it covers.
public class HarnessInputHashTests
{
    private static readonly DateTime FirstSeen = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Synced = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    // Strictly after Synced's date, so the default fixture emote starts out on the "before the
    // window" (stable) side of the predicate.
    private static readonly DateOnly WindowFrom = new(2026, 2, 4);

    [Fact]
    public void SameInputInAnotherOrder_HashesTheSame()
    {
        var forward = HarnessInputHash.Compute(
            [Emote("a"), Emote("b")],
            [Row("a", 1, 5, 0, 0), Row("b", 2, 7, 1, 3)],
            new HashSet<string> { "1", "2" },
            WindowFrom);

        var backward = HarnessInputHash.Compute(
            [Emote("b"), Emote("a")],
            [Row("b", 2, 7, 1, 3), Row("a", 1, 5, 0, 0)],
            new HashSet<string> { "2", "1" },
            WindowFrom);

        Assert.Equal(forward, backward);
    }

    // The core of the fix (#69 Task 8a): Task 8's live verification found LastSyncedAt moving on
    // essentially every 60-second resync tick for a real channel (a 7TV duplicate-set-entry bug
    // upstream of the harness, see HarnessInputHash's class doc) without the row's actual content
    // changing. A timestamp move that stays on the same side of the window boundary must not touch
    // the hash, or a resumable run becomes unresumable in exactly the case the hash exists to guard.
    [Fact]
    public void LastSyncedAtMovingWithinTheSameSideOfTheWindow_DoesNotChangeTheHash()
    {
        var before = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1" }, WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a") with { LastSyncedAt = Synced.AddHours(10) }],
            [Row("a", 1, 5, 0, 0)],
            new HashSet<string> { "1" },
            WindowFrom);

        Assert.Equal(before, after);
    }

    // The other direction, and just as load-bearing: a LastSyncedAt that crosses the window boundary
    // moves the emote between ReplayFidelityCalculator's stable and unstable subsets, which changes
    // what the run actually computes — the identity must change too.
    [Fact]
    public void LastSyncedAtCrossingTheWindowBoundary_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1" }, WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a") with { LastSyncedAt = WindowFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) }],
            [Row("a", 1, 5, 0, 0)],
            new HashSet<string> { "1" },
            WindowFrom);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnAdditionalBotId_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1" }, WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1", "2" }, WindowFrom);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AChangedBotUseCount_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1" }, WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 1, 0)], new HashSet<string> { "1" }, WindowFrom);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AChangedSharedChatUseCount_ChangesTheHash()
    {
        // The #73 addition: the third column has to move the hash exactly like BotUseCount does
        // above, or a resume could continue against a snapshot whose foreign-room usage has since
        // moved in Postgres.
        var before = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 0)], new HashSet<string> { "1" }, WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a")], [Row("a", 1, 5, 0, 1)], new HashSet<string> { "1" }, WindowFrom);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnArchivedFlagWithoutADate_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute([Emote("a")], [], new HashSet<string>(), WindowFrom);
        var after = HarnessInputHash.Compute(
            [Emote("a") with { IsArchived = true }], [], new HashSet<string>(), WindowFrom);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TheHash_IsALowercaseSha256Hex()
    {
        var hash = HarnessInputHash.Compute([Emote("a")], [], new HashSet<string>(), WindowFrom);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }

    private static EmoteLifetimeDto Emote(string id) =>
        new(id, "Name" + id, false, FirstSeen, null, Synced);

    private static UsageStatRowDto Row(string emoteId, int day, int useCount, int botUseCount, int sharedChatUseCount) =>
        new(emoteId, new DateOnly(2026, 9, day), useCount, botUseCount, sharedChatUseCount);
}
