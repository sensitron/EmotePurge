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

    [Fact]
    public void SameInputInAnotherOrder_HashesTheSame()
    {
        var forward = HarnessInputHash.Compute(
            [Emote("a"), Emote("b")],
            [Row("a", 1, 5, 0), Row("b", 2, 7, 1)],
            new HashSet<string> { "1", "2" });

        var backward = HarnessInputHash.Compute(
            [Emote("b"), Emote("a")],
            [Row("b", 2, 7, 1), Row("a", 1, 5, 0)],
            new HashSet<string> { "2", "1" });

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void AChangedLastSyncedAt_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute([Emote("a")], [Row("a", 1, 5, 0)], new HashSet<string> { "1" });
        var after = HarnessInputHash.Compute(
            [Emote("a") with { LastSyncedAt = Synced.AddSeconds(1) }],
            [Row("a", 1, 5, 0)],
            new HashSet<string> { "1" });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnAdditionalBotId_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute([Emote("a")], [Row("a", 1, 5, 0)], new HashSet<string> { "1" });
        var after = HarnessInputHash.Compute([Emote("a")], [Row("a", 1, 5, 0)], new HashSet<string> { "1", "2" });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AChangedBotUseCount_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute([Emote("a")], [Row("a", 1, 5, 0)], new HashSet<string> { "1" });
        var after = HarnessInputHash.Compute([Emote("a")], [Row("a", 1, 5, 1)], new HashSet<string> { "1" });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AnArchivedFlagWithoutADate_ChangesTheHash()
    {
        var before = HarnessInputHash.Compute([Emote("a")], [], new HashSet<string>());
        var after = HarnessInputHash.Compute([Emote("a") with { IsArchived = true }], [], new HashSet<string>());

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TheHash_IsALowercaseSha256Hex()
    {
        var hash = HarnessInputHash.Compute([Emote("a")], [], new HashSet<string>());

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }

    private static EmoteLifetimeDto Emote(string id) =>
        new(id, "Name" + id, false, FirstSeen, null, Synced);

    private static UsageStatRowDto Row(string emoteId, int day, int useCount, int botUseCount) =>
        new(emoteId, new DateOnly(2026, 9, day), useCount, botUseCount);
}
