using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Worker.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The run itself: preconditions, resume, the abort exit codes and the idempotent closing step. The
// archive client and both query services are substituted, so no test here opens a socket — what is
// under test is the decision sequence around them, which is exactly where a wrong exit code would
// turn "the harness stopped" into "the harness said the import is fine".
public class HarnessRunnerTests : IDisposable
{
    private const string ChannelId = "channel-guid";
    private const string TwitchChannelId = "12345";
    private const string ChannelName = "brudivoeller_tv";

    // The process start; the window therefore ends on 2026-09-04 (yesterday, the last complete UTC
    // day) and, at --days 3, starts on 2026-09-02.
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day1 = new(2026, 9, 2);
    private static readonly DateOnly Day2 = new(2026, 9, 3);
    private static readonly DateOnly Day3 = new(2026, 9, 4);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "emotepurge-harness-run-" + Guid.NewGuid().ToString("N"));

    // One clock for the whole test, not one per Run(...) call: the resume tests need to move the
    // process start between two invocations of the same file.
    private readonly FakeClock _clock = new(Now);

    private readonly IChannelService _channels = Substitute.For<IChannelService>();
    private readonly IUsageStatQueryService _usage = Substitute.For<IUsageStatQueryService>();
    private readonly IChatLogArchiveClient _archive = Substitute.For<IChatLogArchiveClient>();
    private readonly IBotChatterDetector _bots = Substitute.For<IBotChatterDetector>();

    public HarnessRunnerTests()
    {
        Directory.CreateDirectory(_directory);

        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>()).Returns(NewChannel());
        _usage.GetEmoteLifetimesAsync(ChannelId, Arg.Any<CancellationToken>()).Returns(Lifetimes());
        _usage.GetRowsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Rows());
        _usage.GetEarliestBotUsageDateAsync(ChannelId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 9, 1));
        _bots.KnownBotAccountIds.Returns(new HashSet<string> { "19264788" });
        _bots.IsBot(Arg.Any<string?>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>()).Returns(false);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingTwitchChannelId_AbortsBeforeTheFirstFetch()
    {
        // #34/#44: no login fallback. Without the immutable id there is nothing safe to address.
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>())
            .Returns(NewChannel(twitchChannelId: null));

        var exitCode = await Run(3);

        Assert.Equal(3, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AnUnknownChannel_Aborts()
    {
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>()).Returns((Channel?)null);

        Assert.Equal(3, await Run(3));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task AMeasurementShorterThanTheWindow_Aborts()
    {
        // Joined on 2026-09-02, so the first fully tracked day is 2026-09-03 — two days, not three.
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>())
            .Returns(NewChannel(createdAt: new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(3, await Run(3));
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AWindowWithoutASingleLogDay_AbortsButKeepsTheFileThatProvesTheRequests()
    {
        RespondWith(_ => NoLogDay());

        Assert.Equal(3, await Run(3));

        var file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        Assert.Equal(3, File.ReadAllLines(file).Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task LogsWithoutBadgesAndWithoutUserIds_EndTheRunAsUndecidable()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(new ChatLogMessage(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), null, [], "12345", null, "PogChamp"));
            return CompleteDay(1);
        });

        var exitCode = await Run(3);

        Assert.Equal(5, exitCode);
        // Only the first day was fetched; the verdict is "reassess the approach", not "fetch more".
        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Arg.Any<DateOnly>(), Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        var file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        Assert.DoesNotContain("\"kind\":\"day\"", File.ReadAllText(file));
    }

    [Fact]
    public async Task ANoBadgesNoUserIdsAbort_SpendsFromTheCapUsingTheReceivedBytes()
    {
        // The day was read to completion (Complete, MessageCount > 0) before the fallback fired, so
        // it already cost the archive real bytes — the same accounting the TransportFailure/
        // ByteCapExceeded paths get via the `default:` branch (see
        // ADayThatAbortedWithoutABodyStillSpendsFromTheCap...). Befund: this call dropped the
        // `bytes` argument, so the event line read 0 and a resume would see the full cap again
        // instead of what this already-spent request actually cost.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(new ChatLogMessage(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), null, [], "12345", null, "PogChamp"));
            return CompleteDay(1, bytes: 900_000);
        });

        Assert.Equal(5, await Run(3, maxMegabytes: 1));

        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var noBadgesEvent = Assert.Single(
            new HarnessReportFile(path).ReadDays().Events, e => e.Status == "NoBadgesNoUserIds");
        Assert.Equal(900_000, noBadgesEvent.Bytes);

        // Second run: a compliant day now answers, but the cap must already reflect the 900 KB the
        // first, undecidable attempt spent — not the full 1 MB again.
        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        Assert.Equal((1L * 1024 * 1024) - 900_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task ARateLimitedDay_StopsWithAResumePoint_AndTheNextRunContinuesThere()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3));

        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"event\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));

        // Second run, same substitutes, but the archive answers day 3 now.
        _archive.ClearReceivedCalls();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Day3, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ReadDayAsync(
            TwitchChannelId, Day1, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ReadDayAsync(
            TwitchChannelId, Day2, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());

        var reportPath = Assert.Single(Directory.GetFiles(_directory, "*.report.json"));
        var afterSecondRun = File.ReadAllBytes(reportPath);

        // The 429 of the first run survives in the file and reaches both reports as a *number*.
        // Asserting on the string "429" alone would also match the label of the markdown row and
        // pass with zero throttled requests.
        Assert.Contains("\"rateLimitedDays\": 1", File.ReadAllText(reportPath));
        Assert.Contains("\"resumePoint\": \"2026-09-04\"", File.ReadAllText(reportPath));
        Assert.Contains("| HTTP 429 | 1 |", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md"))));

        // Third run: everything is on disk, nothing is fetched, and the machine-readable report is
        // byte-identical — that is the "closing step is repeatable" promise of the failure-mode table.
        _archive.ClearReceivedCalls();
        Assert.Equal(0, await Run(3));

        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
        Assert.Equal(afterSecondRun, File.ReadAllBytes(reportPath));
    }

    [Fact]
    public async Task ATransportFailure_StopsWithExitFourAndWithoutADayLine()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 17, null, 0, 0, 0, 502);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"event\"")));
    }

    [Fact]
    public async Task ACancelledRun_StopsWithExitFourAndKeepsTheDaysItHad()
    {
        using var cts = new CancellationTokenSource();
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3, cts.Token));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
    }

    [Fact]
    public async Task TheByteCap_ShrinksWithEveryDayAndStopsTheRunWhenItIsSpent()
    {
        var offered = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 600_000);
        }, offered);

        // 1 MB cap, 600 KB per day: day 1 fits, day 2 fits (the cap is checked before the request,
        // not predicted), day 3 finds nothing left.
        Assert.Equal(4, await Run(3, maxMegabytes: 1));

        Assert.Equal(2, offered.Count);
        Assert.Equal(1L * 1024 * 1024, offered[0]);
        Assert.Equal(1L * 1024 * 1024 - 600_000, offered[1]);
    }

    [Fact]
    public async Task ADayThatAbortedWithoutABodyStillSpendsFromTheCap_AndTheNextRunKnowsIt()
    {
        // Day 1 fails via TransportFailure after receiving 900 KB — a wasted transfer that produces
        // only an event line, never a day line. If those bytes were forgotten on resume (Befund 1),
        // the second run would see the full 1 MB cap again instead of the roughly 148 KB actually
        // left; five such resumes could each burn a fresh cap's worth against a service that never
        // agreed to any of it.
        RespondWith(_ => new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 900_000, null, 0, 0, 0, 502));

        Assert.Equal(4, await Run(3, maxMegabytes: 1));
        Assert.DoesNotContain(
            Directory.GetFiles(_directory, "*.jsonl").SelectMany(File.ReadAllLines),
            l => l.Contains("\"kind\":\"day\""));

        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        // 1 MB minus the 900 KB already wasted by the aborted first attempt, not the full 1 MB again.
        Assert.Equal((1L * 1024 * 1024) - 900_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task ACompleteRun_WritesBothReportsAndTheGateFieldsOfTheCalculator()
    {
        // Day 2 alone carries a SourceRoomId distinct from RoomId, so exactly one of the three
        // messages is a shared-chat hit — this is the third seam of Befund 2 (Abschluss-Review): a
        // mapping bug that swapped RoomId and SourceRoomId in the `counter.Count(...)` call at the
        // HarnessRunner call site would mark every message as shared chat instead of exactly one,
        // which the asserted sharedChatMessages below catches. A day where both fields were the same
        // (or both null, as the previous fixture had it) could not tell the two apart.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp", sourceRoomId: day == Day2 ? "other-room" : null));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"gateEligible\"", json);
        Assert.Contains("\"totalDeviation\"", json);
        Assert.Contains("\"top20Recall\"", json);
        // A three-day run can never be a gate run — the pre-registration fixes the window at 30.
        Assert.Contains("window-not-30-days", json);
        // The window length the run was started with reaches the report unchanged; a report that
        // claimed 30 here would read as a gate run.
        Assert.Contains("\"windowDays\": 3", json);
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);
        Assert.Contains("\"runComplete\": true", json);
        Assert.Contains("\"ratedDays\": 3", json);
        // Befund 2 (Abschluss-Review): these three values, not just their key names, pin the three
        // mapping seams between the query DTOs and the harness's own replay types. Each was verified
        // to fail under its corresponding one-line mutation at the HarnessRunner call sites (see the
        // final-fix report) before this test was written this way.
        Assert.Contains("\"humanLogTotal\": 3", json); // one PogChamp hit per day, three rated days
        Assert.Contains("\"humanLiveTotal\": 3", json); // UseCount=1 per day; BotUseCount=7 must not leak in
        Assert.Contains("\"sharedChatMessages\": 1", json); // only day 2's message carries a foreign SourceRoomId

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("Replay-Treue", markdown);
        Assert.Contains("Uptime Kuma", markdown);
        Assert.Contains(ChannelName, markdown);
    }

    [Fact]
    public async Task AResumedRun_ReportsTheDistinctChatterCountAsUnavailable()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("wiederaufgenommen", markdown);
    }

    [Fact]
    public async Task EveryDayOfTheWindow_IsFetchedExactlyOnce()
    {
        // Not a claim about the client instance — the runner takes that once through its constructor,
        // which is structural and needs no test. This is about the loop: three days, three requests,
        // no day asked for twice.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        await Run(3);

        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
        foreach (var day in new[] { Day1, Day2, Day3 })
        {
            await _archive.Received(1).ReadDayAsync(
                TwitchChannelId, day, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AChangedDataSnapshot_StartsANewFileInsteadOfContinuingTheOldOne()
    {
        // The Codex-adversarial finding the input hash exists for: the live worker keeps writing
        // while the harness runs, and a resume against a different snapshot would mix day counts
        // taken against two different databases.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // One live usage row changes; everything else stays.
        _usage.GetRowsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<UsageStatRowDto>>([new("e1", Day1, 99, 0), new("e1", Day2, 1, 0), new("e1", Day3, 1, 0)]);
        _archive.ClearReceivedCalls();

        Assert.Equal(0, await Run(3));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(firstFile, files);
        // And the new run really fetched all three days again rather than inheriting them.
        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    [Fact]
    public async Task ADamagedReportFile_EndsAsAViolatedPreconditionRatherThanAStackTrace()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        // A damaged line in the *middle* — not a truncated last line, which is a dropped unfinished
        // day and stays legal.
        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(2, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-0");
        File.WriteAllLines(path, lines);

        Assert.Equal(3, await Run(3));
    }

    [Fact]
    public async Task AConfiguredWindowOutsideTheAllowedRange_Aborts()
    {
        // --days is clamped by the parser; Harness:WindowDays reaches the runner unchecked, and
        // Harness__WindowDays=500 in a compose file would otherwise buy a 500-day run.
        Assert.Equal(3, await Run(500));
        Assert.Equal(3, await Run(0));
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AFailingCountingCallback_EndsWithItsOwnExitCodeAndKeepsTheFinishedDays()
    {
        // Exceptions out of onMessage propagate by the archive client's contract (T3). They are our
        // bug, not the archive's, so they must not be dressed up as "aborted, just run it again" —
        // but they must not reach the operator as a stack trace either.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                await onMessage(new ChatLogMessage(
                    day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), "chatter-1", null!, TwitchChannelId, null, "PogChamp"));
            }
            else
            {
                await onMessage(Message(day, "chatter-1", "PogChamp"));
            }

            return CompleteDay(1);
        });

        Assert.Equal(6, await Run(3));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task ARunContinuedOnTheNextUtcDay_KeepsItsFrozenWindowAndItsFile()
    {
        // The window is derived from the process start, so before this test every invocation on a
        // new UTC day produced a new window, a new identity and therefore a new file — the finished
        // days and the bytes already spent were silently abandoned. That is not an exotic case: 30
        // days of a large channel are ~490 MB against a 200 MB cap, so the binding run *has* to be
        // invoked several times and will cross a midnight.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 900_000, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        });

        Assert.Equal(4, await Run(3, maxMegabytes: 1));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // Next day, same command line.
        _clock.Now = Now.AddDays(1);
        _archive.ClearReceivedCalls();
        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        // Same file, and the window did not slide to 2026-09-03..2026-09-05.
        Assert.Equal(firstFile, Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);

        // Only the throttled day is fetched again; the two finished ones are not.
        Assert.Equal(1, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Day3, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());

        // And the cap still knows what the first run spent: 2 x 10 KB of day lines plus the 900 KB
        // the throttled attempt cost, not a fresh megabyte.
        Assert.Equal((1L * 1024 * 1024) - 920_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task AnUnfinishedRunOlderThanTheResumeLimit_IsLeftAloneAndANewMeasurementStarts()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));
        var abandoned = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // Eight days later the frozen window no longer describes anything the operator asked about;
        // a report dated today would answer for a window nobody chose.
        _clock.Now = Now.AddDays(8);
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(abandoned, files);
        Assert.Contains(
            "\"windowTo\": \"2026-09-12\"",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json"))));
    }

    [Fact]
    public async Task AResumeWithADifferentWindowLength_StartsANewFile()
    {
        // The frozen window carries its own length, so a --days that no longer matches it cannot be
        // continued — it is a different measurement and gets its own file.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(2));

        Assert.Equal(2, Directory.GetFiles(_directory, "*.jsonl").Length);
        Assert.Contains(
            "\"windowFrom\": \"2026-09-03\"",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json"))));
    }

    private Task<int> Run(int days, CancellationToken ct = default, int maxMegabytes = 200)
    {
        var runner = new HarnessRunner(
            _channels,
            _usage,
            _archive,
            _bots,
            new HarnessOptions { OutputDirectory = _directory, MaxMegabytesPerRun = maxMegabytes, WindowDays = 30 },
            _clock,
            NullLogger<HarnessRunner>.Instance);

        return runner.RunAsync(ChannelName, days, ct);
    }

    private void RespondWith(
        Func<DateOnly, Func<ChatLogMessage, ValueTask>, Task<ChatLogDayResult>> respond,
        List<long>? offeredMaxBytes = null)
    {
        _archive.ReadDayAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<long>(),
                Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                offeredMaxBytes?.Add(call.ArgAt<long>(2));
                return respond(call.ArgAt<DateOnly>(1), call.ArgAt<Func<ChatLogMessage, ValueTask>>(3));
            });
    }

    private void RespondWith(Func<DateOnly, ChatLogDayResult> respond) =>
        RespondWith((day, _) => Task.FromResult(respond(day)));

    private static ChatLogDayResult NoLogDay() => new(ChatLogDayStatus.NoLogDay, 0, null, 0, 0, 0, 404);

    private static ChatLogDayResult CompleteDay(int messageCount, long bytes = 1024) =>
        new(ChatLogDayStatus.Complete, bytes, "deadbeef", messageCount, 0, 0, 200);

    private static ChatLogMessage Message(DateOnly day, string userId, string text, string? sourceRoomId = null) =>
        new(
            day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            userId,
            [new KeyValuePair<string, string>("subscriber", "1")],
            TwitchChannelId,
            sourceRoomId,
            text);

    private static Channel NewChannel(
        string? twitchChannelId = TwitchChannelId,
        DateTime? createdAt = null) =>
        new()
        {
            Id = ChannelId,
            TwitchChannelId = twitchChannelId,
            ChannelName = ChannelName,
            IsBotActive = true,
            CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    // Asymmetric on purpose (Befund 2 of the Abschluss-Review): FirstSeenAt and ArchivedAt are both
    // DateTime? and, before this fixture, both took the same-looking round value, so a mapping bug
    // that swapped them at the HarnessRunner call site (`new ReplayEmote(e.Id, e.Name, e.IsArchived,
    // e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt)`) was invisible to every test — CoversDay would
    // fall to false for every window day, matching zero hits, and ACompleteRun_ below asserted only
    // that "totalDeviation" existed as a key, never its value. With FirstSeenAt well before the
    // window and ArchivedAt well after it, the correct mapping still covers every window day (same
    // behaviour as before); the swapped mapping excludes every window day instead, which the
    // asserted humanLogTotal below then catches. LastSyncedAt stays a third, distinct value so a
    // three-way rotation of the same three fields would be caught too.
    private static IReadOnlyList<EmoteLifetimeDto> Lifetimes() =>
    [
        new(
            "e1",
            "PogChamp",
            false,
            Day1.AddDays(-5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Day3.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    ];

    // BotUseCount = 7, not 0 (Befund 2): a mapping bug that swapped UseCount and BotUseCount at the
    // HarnessRunner call site (`new ReplayUsageRow(r.EmoteId, r.Date, r.UseCount, r.BotUseCount)`)
    // used to be invisible — zeroing an already-zero BotUseCount changes nothing a test can see.
    // With BotUseCount nonzero, the swap inflates the human-live side (the gate's denominator) from
    // 3 to 21 over the three rated days, which the asserted humanLiveTotal below catches.
    private static IReadOnlyList<UsageStatRowDto> Rows() =>
    [
        new("e1", Day1, 1, 7),
        new("e1", Day2, 1, 7),
        new("e1", Day3, 1, 7)
    ];

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
