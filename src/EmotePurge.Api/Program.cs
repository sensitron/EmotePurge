using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmotePurge.Api.Auth;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // Default cookie-auth behavior redirects to a login page on 401/403 — wrong for a JSON API.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/channels/{channelName}/join", async (
    string channelName,
    IChannelService channelService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var channel = await channelService.JoinAsync(channelName, ct);
    return Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive });
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapDelete("/api/channels/{channelName}", async (
    string channelName,
    IChannelService channelService,
    CancellationToken ct) =>
{
    var removed = await channelService.LeaveAsync(channelName, ct);
    return removed ? Results.NoContent() : Results.NotFound();
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapGet("/api/channels/{channelName}/usage-stats", async (
    string channelName,
    IUsageStatQueryService usageStatQueryService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var stats = await usageStatQueryService.GetUsageStatsAsync(channelName, ct);
    return Results.Ok(stats);
});

app.MapGet("/api/channels/{channelName}/usage-stats/totals", async (
    string channelName,
    string from,
    string to,
    IUsageStatQueryService usageStatQueryService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
        !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
    {
        return Results.BadRequest(new { error = "'from'/'to' must be ISO dates (yyyy-MM-dd)." });
    }

    if (fromDate > toDate)
    {
        return Results.BadRequest(new { error = "'from' must be on or before 'to'." });
    }

    const int maxRangeDays = 366;
    if (toDate.DayNumber - fromDate.DayNumber > maxRangeDays)
    {
        return Results.BadRequest(new { error = $"Range too large; max {maxRangeDays} days." });
    }

    var totals = await usageStatQueryService.GetUsageTotalsAsync(channelName, fromDate, toDate, ct);
    return Results.Ok(totals);
});

const string OAuthStateCookieName = "ep_oauth_state";
const string TwitchOAuthScope = "user:read:email user:read:moderated_channels";

app.MapGet("/api/auth/twitch/login", (HttpContext httpContext, IConfiguration configuration) =>
{
    var clientId = configuration["Auth:Twitch:ClientId"]
        ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:ClientId' fehlt.");
    var redirectUri = configuration["Auth:Twitch:RedirectUri"]
        ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    httpContext.Response.Cookies.Append(OAuthStateCookieName, state, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = httpContext.Request.IsHttps,
        Expires = DateTimeOffset.UtcNow.AddMinutes(5)
    });

    var authorizeUrl = "https://id.twitch.tv/oauth2/authorize" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString(TwitchOAuthScope)}" +
        $"&state={state}";

    return Results.Redirect(authorizeUrl);
});

app.MapGet("/api/auth/twitch/callback", async (
    HttpContext httpContext,
    string? code,
    string? state,
    IConfiguration configuration,
    ITwitchAuthClient authClient,
    ITwitchHelixClient helixClient,
    IUserService userService,
    CancellationToken ct) =>
{
    var expectedState = httpContext.Request.Cookies[OAuthStateCookieName];
    httpContext.Response.Cookies.Delete(OAuthStateCookieName);

    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expectedState is null || state != expectedState)
    {
        return Results.BadRequest(new { error = "Invalid or missing OAuth state." });
    }

    var redirectUri = configuration["Auth:Twitch:RedirectUri"]
        ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

    var token = await authClient.ExchangeAuthorizationCodeAsync(code, redirectUri, ct);
    if (token is null)
    {
        return Results.BadRequest(new { error = "Twitch token exchange failed." });
    }

    var userInfo = await helixClient.GetUserInfoAsync(token.AccessToken, ct);
    if (userInfo is null)
    {
        return Results.BadRequest(new { error = "Could not resolve Twitch user info." });
    }

    await userService.UpsertLoginAsync(userInfo.Id, userInfo.Login, userInfo.DisplayName, ct);

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, userInfo.Id),
        new(TwitchClaimTypes.Login, userInfo.Login),
        new(TwitchClaimTypes.DisplayName, userInfo.DisplayName),
        new(TwitchClaimTypes.AccessToken, token.AccessToken),
        new(TwitchClaimTypes.TokenExpiresAtUtc, token.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture))
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Ok(new { userInfo.Login, userInfo.DisplayName });
});

app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
    login = user.FindFirstValue(TwitchClaimTypes.Login),
    displayName = user.FindFirstValue(TwitchClaimTypes.DisplayName),
    tokenExpiresAtUtc = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc)
})).RequireAuthorization();

app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/worker/health", async (IConnectionMultiplexer redis) =>
{
    // Redis-Key wird vom Worker periodisch mit TTL geschrieben (s. WorkerHealthPublisher) —
    // Api/Worker kommunizieren dadurch nicht direkt miteinander. Läuft der Worker nicht (mehr)
    // oder hängt der Health-Publisher, läuft der Key einfach ab; das ist dann selbst das Signal.
    var value = await redis.GetDatabase().StringGetAsync("worker:health:twitch");
    if (value.IsNullOrEmpty)
    {
        return Results.Ok(new { status = "unknown", reason = "Kein aktueller Health-Status vom Worker (Key abgelaufen oder Worker nicht gestartet)." });
    }

    var payload = JsonSerializer.Deserialize<WorkerHealthPayload>((string)value!, JsonSerializerOptions.Web);
    if (payload is null)
    {
        return Results.Ok(new { status = "unknown", reason = "Health-Status konnte nicht gelesen werden." });
    }

    var secondsSinceLastMessage = payload.LastMessageReceivedUtc is { } lastMessage
        ? (int)(DateTime.UtcNow - lastMessage).TotalSeconds
        : (int?)null;

    return Results.Ok(new
    {
        status = payload.IsConnected ? "connected" : "disconnected",
        payload.IsConnected,
        payload.LastMessageReceivedUtc,
        secondsSinceLastMessage,
    });
});

app.Run();

internal sealed record WorkerHealthPayload(bool IsConnected, DateTime? LastMessageReceivedUtc);
