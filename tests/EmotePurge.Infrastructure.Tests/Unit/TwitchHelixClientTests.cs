using System.Net;
using System.Text;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Twitch;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Arbitration finding (feat/channel-identity-44): <see cref="TwitchHelixClient.GetUsersAsync"/>
/// only caught <see cref="HttpRequestException"/> and <see cref="TaskCanceledException"/>, so a
/// 200 response with a malformed/truncated body threw <see cref="System.Text.Json.JsonException"/>
/// straight through <c>LookupByLoginAsync</c>/<c>ChannelService.JoinAsync</c> into the global
/// exception handler — a 500 instead of the "Unavailable, carry on as before" contract both callers
/// document. This pins the fix: such a body must come back as <c>null</c>, not throw.
/// </summary>
public class TwitchHelixClientTests
{
    [Fact]
    public async Task GetUsersAsync_WithMalformedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        var client = CreateClient(HttpStatusCode.OK, "{not valid json", "application/json");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsersAsync_WithTruncatedJsonBody_ReturnsNull_InsteadOfThrowing()
    {
        // A body cut off mid-stream — e.g. a dropped connection after the status line landed but
        // before the payload finished — is valid-looking prefix, invalid JSON overall.
        var client = CreateClient(HttpStatusCode.OK, "{\"data\":[{\"id\":\"12345\",\"login\":\"foo", "application/json");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsersAsync_WithWrongContentType_ReturnsNull_InsteadOfThrowing()
    {
        // A 200 answer that isn't actually Helix — e.g. an intermediary's HTML error page delivered
        // with a success status. ReadFromJsonAsync throws NotSupportedException for this, not
        // JsonException, because it never gets far enough to parse.
        var client = CreateClient(HttpStatusCode.OK, "<html><body>Service Unavailable</body></html>", "text/html");

        var result = await client.GetUsersAsync(["12345"], [], "app-token");

        Assert.Null(result);
    }

    private static TwitchHelixClient CreateClient(HttpStatusCode statusCode, string body, string contentType)
    {
        var handler = new StubHandler(statusCode, body, contentType);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twitch.tv/helix/") };
        return new TwitchHelixClient(httpClient, new RecordingLogger<TwitchHelixClient>());
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            return Task.FromResult(response);
        }
    }
}
