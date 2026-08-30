using System.Net;
using System.Text;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The two corrected acceptance criteria from the rate-limit architecture spec
/// (<c>docs/superpowers/specs/2026-08-30-rate-limit-architecture-design.md</c>, "Abnahmekriterien und
/// Harness"), run as <c>WebApplicationFactory</c> tests over the real <c>Program.cs</c> pipeline,
/// following the pattern established by <see cref="RateLimitRejectionTests"/>. Both were written red
/// and stayed red until the routes were rehung: while the <c>ExternalApi</c> policy (40 permits per
/// minute) still covered every route they exercise, both ran out of budget mid-run. They went green
/// with the move to <c>InteractiveRead</c> and <c>Voting</c>. Neither test asserts on a policy name;
/// both measure only response status codes, per the plan's "keine Policy-Namen raten".
/// </summary>
public class RateLimitPolicyBudgetTests : IClassFixture<ApiFactory>
{
    private const string Channel = "testchannel";

    private readonly ApiFactory _factory;

    public RateLimitPolicyBudgetTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <c>docs/superpowers/2026-08-30-rate-limit-baseline-messung.md</c>, Ablauf (b): a round trip from
    /// the overview into a channel's workspace and back cost 7 permits under the since-removed
    /// <c>ExternalApi</c> policy
    /// (permissions, duplicate-names, two active-set reads — the range-resolution quirk documented
    /// there — totals, series, and the return-trip /mine). The baseline's "Folge für die
    /// Abnahmekriterien" section found the spec's original threshold of six round trips already green
    /// without any code change: a cold client-side permissions cache means only the first of several
    /// dense round trips costs the full 7, so six sum to just 38 — under the 40-permit window. It
    /// recommends twelve round trips instead: reliably over that budget, and still comfortably inside
    /// the <c>InteractiveRead</c> capacity of 300 at 5 tokens/s refill it now runs on. This test drives the
    /// same seven requests directly through <c>HttpClient</c> rather than through the Angular app, so
    /// there is no client-side permissions cache here at all — every one of the twelve round trips
    /// below costs the full seven counting requests, which only sharpens the point.
    /// </summary>
    [Fact]
    public async Task TwelveWorkspaceRoundTripsInOneMinute_ProduceNoLocal429()
    {
        using var client = _factory.CreateClient();
        const string userId = "rate-limit-budget-workspace";

        var statusCodes = new List<HttpStatusCode>();
        for (var roundTrip = 0; roundTrip < 12; roundTrip++)
        {
            statusCodes.AddRange(await RunWorkspaceRoundTripAsync(client, userId));
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusCodes);
    }

    /// <summary>
    /// Spec "Abnahmekriterien und Harness": 100 vote mutations in one session must not answer a local
    /// 429, and — because the <c>Voting</c> policy partitions by <c>TwitchUserId + SessionId</c>
    /// rather than by user alone — a request against a different session and a plain navigation route
    /// from the same user must not have been caught in the same budget either. Handler errors (500s
    /// from the unsubstituted vote/channel services) are expected here and not asserted on: what is
    /// asserted is the permit the rate limiter middleware spends before the handler ever runs, exactly
    /// as in <see cref="TwelveWorkspaceRoundTripsInOneMinute_ProduceNoLocal429"/>.
    /// </summary>
    [Fact]
    public async Task HundredVoteMutationsInOneSession_ProduceNoLocal429_AndLeaveOtherTrafficUntouched()
    {
        using var client = _factory.CreateClient();
        const string userId = "rate-limit-budget-voting";

        var voteStatusCodes = new List<HttpStatusCode>();
        for (var vote = 0; vote < 100; vote++)
        {
            using var response = await CastVoteAsync(client, userId, sessionId: 1);
            voteStatusCodes.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, voteStatusCodes);

        // A different session, same user: must not share the budget the 100 mutations above just spent.
        using var otherSessionResponse = await CastVoteAsync(client, userId, sessionId: 2);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherSessionResponse.StatusCode);

        // Plain navigation, same user: voting must not have emptied that budget either.
        using var navigationResponse = await GetAsync(client, userId, $"/api/channels/{Channel}/permissions");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, navigationResponse.StatusCode);
    }

    /// <summary>The seven-request sequence measured as baseline (b): workspace entry, then the return
    /// trip to the overview.</summary>
    private static async Task<List<HttpStatusCode>> RunWorkspaceRoundTripAsync(HttpClient client, string userId)
    {
        var statusCodes = new List<HttpStatusCode>();

        // Hinweg: identical to baseline (a) minus auth/me and worker/health, which carry no policy.
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/permissions")).StatusCode);
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/emotes/duplicate-names")).StatusCode);
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/emotes/active-set")).StatusCode);
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/emotes/active-set")).StatusCode);
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/usage-stats/totals?from=2026-08-01&to=2026-08-30")).StatusCode);
        statusCodes.Add((await GetAsync(client, userId, $"/api/channels/{Channel}/usage-stats/series?from=2026-08-01&to=2026-08-30")).StatusCode);

        // Rückweg: back to the overview.
        statusCodes.Add((await GetAsync(client, userId, "/api/channels/mine")).StatusCode);

        return statusCodes;
    }

    private static Task<HttpResponseMessage> CastVoteAsync(HttpClient client, string userId, long sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/channels/{Channel}/vote-sessions/{sessionId}/votes")
        {
            // An empty body is enough: CastVoteRequest binds EmoteId/Type to their defaults, and the
            // handler's own EmoteIdEmpty check (or VoteEligibilityFilter's 403, reached first with the
            // substituted IVoteEligibilityService) answers long before the rate limiter's budget is the
            // question — the permit is already spent by the time either runs.
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string userId, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }
}
