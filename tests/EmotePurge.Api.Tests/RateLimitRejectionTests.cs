using System.Net;
using System.Text.Json;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The rejected half of the rate limiter, over the real Program.cs pipeline. Until 2026-08-29 there
/// was none: every policy answered a bare 429 — no body, no Retry-After, no log line — so a
/// throttled user got the frontend's generic status message and the server side of the story did not
/// exist at all. Finding out that issues #33/#35 were self-inflicted took two rounds and a read of
/// the production nginx access log; that is what these three assertions are meant to prevent.
/// </summary>
public class RateLimitRejectionTests : IClassFixture<ApiFactory>
{
    /// <summary>The ExternalApi policy's budget, mirrored from Program.cs.</summary>
    private const int ExternalApiPermitLimit = 40;

    /// <summary>
    /// Chosen because both services its handler resolves are substituted in ApiFactory, so a
    /// request costs no database and no Redis — the loop below sends more than eighty of them.
    /// </summary>
    private const string PermissionsPath = "/api/channels/testchannel/permissions";

    private readonly ApiFactory _factory;

    public RateLimitRejectionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExhaustedBudget_Answers429_WithRetryAfterAndAnErrorCode()
    {
        using var client = _factory.CreateClient();

        // A partition key of this test's own: the limiter partitions by the NameIdentifier claim, so
        // sharing one across tests would make the outcome depend on execution order.
        using var response = await ExhaustAsync(client, "rate-limit-contract");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        var retryAfterSeconds = int.Parse(retryAfter);
        // A fixed window of one minute: never longer than the window, never zero (a client told to
        // retry after zero seconds retries straight into the next rejection).
        Assert.InRange(retryAfterSeconds, 1, 60);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.RateLimitExceeded,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryAfterSeconds, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task RequestWithinTheBudget_IsUntouched()
    {
        // The other direction: OnRejected must not run for an accepted request, or every response in
        // the app would carry a Retry-After telling clients to back off from nothing.
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, "rate-limit-headroom");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Rejection_IsLoggedWithPolicyPathAndPartition()
    {
        var log = new CapturingLoggerProvider();
        // Its own host rather than the shared fixture's: a logging provider has to be registered at
        // startup, and a fresh host also means a fresh, unspent limiter.
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(log)));
        using var client = factory.CreateClient();

        using var response = await ExhaustAsync(client, "rate-limit-log");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var entry = Assert.Single(log.Entries, e => e.Category == RateLimitRejection.LogCategory);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // The three things the production investigation had to reconstruct from nginx: which policy,
        // which path, whose budget.
        Assert.Contains("ExternalApi", entry.Message);
        Assert.Contains(PermissionsPath, entry.Message);
        Assert.Contains("rate-limit-log", entry.Message);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PermissionsPath);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    /// <summary>Spends the budget and hands back the first rejected answer.</summary>
    private static async Task<HttpResponseMessage> ExhaustAsync(HttpClient client, string userId)
    {
        // Deliberately more than two windows' worth: a fixed window that happens to roll over
        // mid-loop hands out a second full budget, and a loop of exactly PermitLimit + 1 would then
        // never see a rejection at all. Eighty-one requests cannot fit into two budgets of forty.
        const int attempts = (2 * ExternalApiPermitLimit) + 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var response = await SendAsync(client, userId);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        throw new InvalidOperationException(
            $"Nach {attempts} Anfragen kam keine 429-Antwort — der Rate-Limiter greift nicht.");
    }

    /// <summary>Captures every log entry so a test can assert on one. ILogger has no test double in
    /// this project, and the one thing under test here is that a line is written at all.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        internal sealed record LogEntry(string Category, LogLevel Level, string Message);

        private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
