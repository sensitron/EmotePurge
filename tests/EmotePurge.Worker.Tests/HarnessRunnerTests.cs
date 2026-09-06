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
        // The 429 of the first run survives in the file and reaches the readable report.
        Assert.Contains("429", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md"))));

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
    public async Task ACompleteRun_WritesBothReportsAndTheGateFieldsOfTheCalculator()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
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
    public async Task TheArchiveClient_IsUsedAsASingleInstanceForTheWholeDayLoop()
    {
        // The pacing between two requests is instance state on the client (T3 review finding);
        // a runner that resolved a fresh one per day would silently hammer a free third-party
        // service. The runner takes it once through the constructor, so this is a structural
        // assertion: one instance, every day.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        await Run(3);

        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    private Task<int> Run(int days, CancellationToken ct = default, int maxMegabytes = 200)
    {
        var runner = new HarnessRunner(
            _channels,
            _usage,
            _archive,
            _bots,
            new HarnessOptions { OutputDirectory = _directory, MaxMegabytesPerRun = maxMegabytes, WindowDays = 30 },
            new FakeClock(Now),
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

    private static ChatLogMessage Message(DateOnly day, string userId, string text) =>
        new(
            day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            userId,
            [new KeyValuePair<string, string>("subscriber", "1")],
            TwitchChannelId,
            null,
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

    private static IReadOnlyList<EmoteLifetimeDto> Lifetimes() =>
    [
        new("e1", "PogChamp", false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    ];

    private static IReadOnlyList<UsageStatRowDto> Rows() =>
    [
        new("e1", Day1, 1, 0),
        new("e1", Day2, 1, 0),
        new("e1", Day3, 1, 0)
    ];

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
