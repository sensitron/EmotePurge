using System.Collections;
using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Covers <see cref="ReplayDayCounter"/>, the per-day half of the chat-log backfill harness (#69).
/// Everything here is synthetic: invented emote ids, invented chatter ids, invented message text —
/// no recorded IRC line ever enters the repository (design, Eng-Review 5A).
/// <para>
/// The counter is the place where a silently wrong number would poison the decision about feature
/// B, so each boundary rule (before <c>FirstSeenAt</c>, after <c>ArchivedAt</c>, archived without a
/// date, ambiguous name) gets its own case rather than being folded into one happy-path assertion.
/// </para>
/// </summary>
public class ReplayDayCounterTests
{
    private static readonly DateOnly Day = new(2026, 6, 15);
    private static readonly DateTime Known = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoBadges = [];

    [Fact]
    public void HitBeforeFirstSeenAt_IsNotCountedAndIsReported()
    {
        var counter = Counter([Emote("e1", "Kappa", firstSeenAt: Day.ToDateTime(TimeOnly.MinValue).AddDays(5))]);

        Say(counter, "hello Kappa");
        var line = Finish(counter);

        Assert.Empty(line.HumanCounts);
        Assert.Equal(1, Reason(line, UnmatchedReason.BeforeFirstSeen));
    }

    [Fact]
    public void HitAfterArchivedAt_IsNotCountedAndIsReported()
    {
        var counter = Counter([Emote("e1", "Kappa", Known, archivedAt: Day.ToDateTime(TimeOnly.MinValue).AddDays(-5))]);

        Say(counter, "hello Kappa");
        var line = Finish(counter);

        Assert.Empty(line.HumanCounts);
        Assert.Equal(1, Reason(line, UnmatchedReason.AfterArchived));
    }

    [Fact]
    public void ArchivedWithoutDate_CountsAsAfterArchived()
    {
        // The emote is missing from the day map (design: excluded, counted separately), but it is
        // still in the channel-wide second map, so the reason is "after archived", not "unknown".
        var counter = Counter([Emote("e1", "Kappa", Known, archivedAt: null, isArchived: true)]);

        Say(counter, "Kappa");
        var line = Finish(counter);

        Assert.Empty(line.HumanCounts);
        Assert.Equal(1, Reason(line, UnmatchedReason.AfterArchived));
        Assert.Equal(0, Reason(line, UnmatchedReason.UnknownName));
    }

    [Fact]
    public void UnknownFirstSeenAt_IsCountedAndFlagged()
    {
        var counter = Counter([Emote("e1", "Kappa", firstSeenAt: null)]);

        var category = Say(counter, "hello Kappa");
        var line = Finish(counter);

        Assert.Equal(UsageCategory.Human, category);
        Assert.Equal(1, line.HumanCounts["e1"]);
        Assert.Equal(1, line.FirstSeenUnknownHits);
    }

    [Fact]
    public void UnknownName_IsReportedPerDistinctToken()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "totally random random");
        var line = Finish(counter);

        Assert.Equal(2, Reason(line, UnmatchedReason.UnknownName));
    }

    [Fact]
    public void AmbiguousName_CountsOnTheCoalescedIdAndIsMarked()
    {
        var counter = Counter([Emote("a1", "Kappa", Known), Emote("a2", "Kappa", Known)]);

        Say(counter, "hello Kappa");
        var line = Finish(counter);

        Assert.Equal(1, line.HumanCounts["a1"]);
        Assert.False(line.HumanCounts.ContainsKey("a2"));
        Assert.Equal(1, Reason(line, UnmatchedReason.AmbiguousName));
    }

    [Fact]
    public void RepeatedEmoteInOneMessage_CountsOnce()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa Kappa Kappa");
        var line = Finish(counter);

        Assert.Equal(1, line.HumanCounts["e1"]);
    }

    [Fact]
    public void BotMessage_CountsIntoBotCountsOnly()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)], (_, _) => true);

        var category = Say(counter, "Kappa");
        var line = Finish(counter);

        Assert.Equal(UsageCategory.Bot, category);
        Assert.Equal(1, line.BotCounts["e1"]);
        Assert.Empty(line.HumanCounts);
        Assert.Equal(1, line.BotMessageCount);
        Assert.Equal(0, line.CellCount);
        Assert.Equal(0, line.DistinctChatters);
    }

    [Fact]
    public void SharedChatMessage_IsCountedAndMarked()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        // Foreign: sourceRoomId differs from roomId ("room1", Say's default).
        Say(counter, "Kappa", sourceRoomId: "other-room");
        // Own: sourceRoomId equals roomId — a Shared Chat session's origin channel tags its own
        // messages too, and those still count as human.
        Say(counter, "Kappa", userId: "u2", sourceRoomId: "room1");

        var line = Finish(counter);

        Assert.Equal(1, line.SharedChatCounts["e1"]);
        Assert.Equal(1, line.HumanCounts["e1"]);
        Assert.Equal(1, line.SharedChatMessageCount);
    }

    [Fact]
    public void ForeignBotMessage_CountsIntoSharedChatCountsOnly()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)], (_, _) => true);

        var category = Say(counter, "Kappa", sourceRoomId: "other-room");
        var line = Finish(counter);

        Assert.Equal(UsageCategory.SharedChat, category);
        Assert.Equal(1, line.SharedChatCounts["e1"]);
        Assert.Equal(0, line.BotMessageCount);
        Assert.Empty(line.BotCounts);
    }

    [Fact]
    public void ForeignChatter_DoesNotAppearInDistinctChattersOrCells()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa", userId: "foreign-user", sourceRoomId: "other-room");
        var line = Finish(counter);

        Assert.Equal(0, line.DistinctChatters);
        Assert.Equal(0, line.CellCount);
    }

    [Fact]
    public void ForeignMessage_DoesNotAffectFirstSeenUnknownHitsOrUnknownNameReason()
    {
        // FirstSeenAt: null would flag every human/bot hit as FirstSeenUnknownHits; "unmatched" is an
        // unmatched word that would flag UnknownName if this message were tokenized like an own one.
        var counter = Counter([Emote("e1", "Kappa", firstSeenAt: null)]);

        Say(counter, "Kappa unmatched", sourceRoomId: "other-room");
        var line = Finish(counter);

        Assert.Equal(1, line.SharedChatCounts["e1"]);
        Assert.Equal(0, line.FirstSeenUnknownHits);
        Assert.Equal(0, Reason(line, UnmatchedReason.UnknownName));
    }

    [Fact]
    public void IndeterminateMessage_CountsIntoSharedChatCountsAndItsOwnCounter()
    {
        // Shared Chat markers present, but no usable source-room-id: the room cannot be told apart.
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        var category = Say(counter, "Kappa", sourceRoomId: null, hasOtherSourceMarkers: true);
        var line = Finish(counter);

        Assert.Equal(UsageCategory.SharedChat, category);
        Assert.Equal(1, line.SharedChatCounts["e1"]);
        Assert.Equal(1, line.IndeterminateMessageCount);
        Assert.Equal(0, line.SharedChatMessageCount);
    }

    [Fact]
    public void MissingRoomIdWithSourceRoomIdSet_IsForeign()
    {
        // A comparison against nothing is never "equal" — the contract is tested rather than left to
        // string.Equals' null semantics.
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        var category = Say(counter, "Kappa", roomId: null, sourceRoomId: "other-room");
        var line = Finish(counter);

        Assert.Equal(UsageCategory.SharedChat, category);
        Assert.Equal(1, line.SharedChatMessageCount);
        Assert.Equal(1, line.SharedChatCounts["e1"]);
    }

    [Fact]
    public void SharedChatHits_AlwaysHaveAMatchingMessageCounter()
    {
        // Control sum (Nr. 4): sum(SharedChatCounts) > 0 implies SharedChatMessageCount +
        // IndeterminateMessageCount > 0 — a shared-chat hit can never appear with both message
        // counters at zero.
        var counter = Counter([Emote("e1", "Kappa", Known), Emote("e2", "PogU", Known)]);

        Say(counter, "Kappa", sourceRoomId: "other-room");
        Say(counter, "PogU", sourceRoomId: null, hasOtherSourceMarkers: true);
        var line = Finish(counter);

        Assert.True(line.SharedChatCounts.Values.Sum() > 0);
        Assert.True(line.SharedChatMessageCount + line.IndeterminateMessageCount > 0);
        Assert.Equal(1, line.SharedChatMessageCount);
        Assert.Equal(1, line.IndeterminateMessageCount);
    }

    [Fact]
    public void TimestampOnAnotherDay_IsCountedAndMarked()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa", at: Day.ToDateTime(new TimeOnly(0, 0)).AddMinutes(-1));
        var line = Finish(counter);

        Assert.Equal(1, line.HumanCounts["e1"]);
        Assert.Equal(1, line.OutsideDayCount);
    }

    [Fact]
    public void SameChatterTwice_ProducesOneCellWithOneChatter()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa");
        Say(counter, "Kappa");
        var line = Finish(counter);

        Assert.Equal(1, line.CellCount);
        Assert.Equal(1, line.KHistogram[1]);
        Assert.Equal(1, line.DistinctChatters);
    }

    [Fact]
    public void TwoChatters_ProduceOneCellWithTwoChatters()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa", userId: "u1");
        Say(counter, "Kappa", userId: "u2");
        var line = Finish(counter);

        Assert.Equal(1, line.CellCount);
        Assert.Equal(0, line.KHistogram[1]);
        Assert.Equal(1, line.KHistogram[2]);
        Assert.Equal(2, line.DistinctChatters);
    }

    [Fact]
    public void MissingChatterId_CollapsesIntoOnePseudoChatter()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        Say(counter, "Kappa", userId: null);
        Say(counter, "Kappa", userId: "");
        var line = Finish(counter);

        Assert.Equal(1, line.KHistogram[1]);
        Assert.Equal(1, line.DistinctChatters);
    }

    [Fact]
    public void Histogram_CapsAtTenPlus()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);

        for (var i = 0; i < 12; i++)
        {
            Say(counter, "Kappa", userId: $"u{i}");
        }

        var line = Finish(counter);

        Assert.Equal(11, line.KHistogram.Count);
        Assert.Equal(1, line.KHistogram[10]);
        Assert.Equal(1, line.CellCount);
        Assert.Equal(12, line.DistinctChatters);
    }

    [Fact]
    public void Finish_ExposesNoChatterIds()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);
        Say(counter, "Kappa", userId: "chatter-4711");
        var line = Finish(counter);

        foreach (var property in typeof(ReplayDayLine).GetProperties())
        {
            Assert.False(
                typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType),
                $"{property.Name} could carry chatter ids out of the counter");
        }

        Assert.Equal(["e1"], line.HumanCounts.Keys.Order().ToArray());
        Assert.DoesNotContain("chatter-4711", Strings(line));
    }

    [Fact]
    public void Finish_CarriesTheCallerSuppliedFactsThrough()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);
        Say(counter, "Kappa");
        Say(counter, "nothing here");

        var line = counter.Finish(ReplayDayStatuses.Complete, 4242, "abc123", nonPrivmsg: 7, malformed: 3);

        Assert.Equal(Day, line.Day);
        Assert.Equal(ReplayDayStatuses.Complete, line.Status);
        Assert.Equal(4242, line.Bytes);
        Assert.Equal("abc123", line.BodySha256Hex);
        Assert.Equal(7, line.NonPrivmsgLines);
        Assert.Equal(3, line.MalformedLines);
        Assert.Equal(2, line.MessageCount);
    }

    [Fact]
    public void Finish_RefusesASecondCall()
    {
        var counter = Counter([Emote("e1", "Kappa", Known)]);
        counter.Finish(ReplayDayStatuses.NoLog, 0, null, 0, 0);

        Assert.Throws<InvalidOperationException>(() => counter.Finish(ReplayDayStatuses.NoLog, 0, null, 0, 0));
        Assert.Throws<InvalidOperationException>(() => Say(counter, "Kappa"));
    }

    private static ReplayEmote Emote(string id, string name, DateTime? firstSeenAt = null, DateTime? archivedAt = null, bool isArchived = false)
        => new(id, name, isArchived || archivedAt is not null, firstSeenAt, archivedAt, Known);

    private static ReplayDayCounter Counter(
        IReadOnlyList<ReplayEmote> emotes,
        Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool>? isBot = null)
        => new(Day, emotes, isBot ?? ((_, _) => false));

    private static UsageCategory Say(
        ReplayDayCounter counter,
        string text,
        string? userId = "u1",
        DateTime? at = null,
        string? roomId = "room1",
        string? sourceRoomId = null,
        bool hasOtherSourceMarkers = false)
        => counter.Count(
            at ?? Day.ToDateTime(new TimeOnly(12, 0)), userId, NoBadges, roomId, sourceRoomId, hasOtherSourceMarkers, text);

    private static ReplayDayLine Finish(ReplayDayCounter counter)
        => counter.Finish(ReplayDayStatuses.Complete, 1024, "sha", 0, 0);

    private static int Reason(ReplayDayLine line, UnmatchedReason reason)
        => line.UnmatchedByReason.GetValueOrDefault(reason.ToString());

    private static IEnumerable<string> Strings(ReplayDayLine line)
    {
        foreach (var property in typeof(ReplayDayLine).GetProperties())
        {
            var value = property.GetValue(line);
            switch (value)
            {
                case string text:
                    yield return text;
                    break;
                case IDictionary dictionary:
                    foreach (var key in dictionary.Keys)
                    {
                        if (key is string keyText)
                        {
                            yield return keyText;
                        }
                    }

                    break;
            }
        }
    }
}
