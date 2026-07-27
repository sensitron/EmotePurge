using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmotePurge.Api.Auth;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
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

// Hinter einem host-level Reverse Proxy (TLS-Termination) erreicht die Verbindung den Container
// über die Docker-Bridge-Gateway-IP, nicht über Loopback — Default-Trust von ForwardedHeadersMiddleware
// (nur Loopback) würde X-Forwarded-Proto sonst ignorieren. Sicher, weil der Container ausschließlich
// über einen 127.0.0.1-gebundenen Host-Port erreichbar ist (einzig möglicher Absender ist der lokale Proxy).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownIPNetworks = { },
    KnownProxies = { }
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/channels/{channelName}", async (
    string channelName,
    IChannelService channelService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var channel = await channelService.GetByNameAsync(channelName, ct);
    return channel is null
        ? Results.NotFound()
        : Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive, channel.ActiveEmoteSetId });
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapGet("/api/channels", async (
    IChannelService channelService,
    CancellationToken ct) =>
{
    var channels = await channelService.ListAllAsync(ct);
    return Results.Ok(channels.Select(c => new
    {
        channelId = c.Id,
        channelName = c.ChannelName,
        c.IsBotActive,
        c.TwitchChannelId,
        c.CreatedAt
    }));
})
.RequireAuthorization()
.AddEndpointFilter<GlobalAdminAuthorizationFilter>();

app.MapGet("/api/channels/mine", async (
    HttpContext httpContext,
    IMyChannelsService myChannelsService,
    CancellationToken ct) =>
{
    var principal = httpContext.User.TryBuildTwitchPrincipal();
    if (principal is null)
    {
        return Results.Unauthorized();
    }

    var result = await myChannelsService.GetMyChannelsAsync(principal, ct);
    return Results.Ok(result);
})
.RequireAuthorization();

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

app.MapPost("/api/channels/{channelName}/emotes/sync-deleted", async (
    string channelName,
    SyncDeletedRequest request,
    IEmoteService emoteService,
    CancellationToken ct) =>
{
    if (request.EmoteIds is null || request.EmoteIds.Count == 0)
    {
        return Results.BadRequest(new { error = "EmoteIds must not be empty." });
    }

    var result = await emoteService.MarkDeletedAsync(channelName, request.EmoteIds, ct);
    return Results.Ok(new { archivedCount = result.ArchivedCount, notFoundIds = result.NotFoundIds });
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
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapPost("/api/channels/{channelName}/vote-sessions", async (
    string channelName,
    CreateVoteSessionRequest request,
    IVoteSessionService voteSessionService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Title must not be empty." });
    }

    if (request.AllowedVoterRoles == 0)
    {
        return Results.BadRequest(new { error = "AllowedVoterRoles must not be empty." });
    }

    if (request.AllowedVoterRoles.HasFlag(AllowedRoles.VIPs))
    {
        return Results.BadRequest(new { error = "AllowedRoles.VIPs is not supported yet (no Twitch self-check API available)." });
    }

    if (request.StartedAt is { } startedAt && startedAt > DateTime.UtcNow)
    {
        return Results.BadRequest(new { error = "StartedAt must not be in the future." });
    }

    var session = await voteSessionService.CreateAsync(channelName, request.Title, request.AllowedVoterRoles, request.StartedAt, ct);
    if (session is null)
    {
        return Results.NotFound(new { error = "Channel not joined." });
    }

    return Results.Ok(new VoteSessionSummaryDto(session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt, session.EndedAt));
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapPost("/api/channels/{channelName}/vote-sessions/{sessionId:long}/end", async (
    string channelName,
    long sessionId,
    IVoteSessionService voteSessionService,
    CancellationToken ct) =>
{
    var session = await voteSessionService.EndAsync(channelName, sessionId, ct);
    if (session is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new VoteSessionSummaryDto(session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt, session.EndedAt));
})
.RequireAuthorization()
.AddEndpointFilter<ChannelManagementAuthorizationFilter>();

app.MapGet("/api/channels/{channelName}/vote-sessions", async (
    string channelName,
    HttpContext httpContext,
    IVoteSessionQueryService voteSessionQueryService,
    IChannelAccessService channelAccessService,
    IVoteEligibilityService voteEligibilityService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var principal = httpContext.User.TryBuildTwitchPrincipal();
    if (principal is null)
    {
        return Results.Unauthorized();
    }

    var sessions = await voteSessionQueryService.ListSessionsAsync(channelName, ct);

    // Managers (admin/broadcaster/live-moderator) see every session unfiltered — same standing as
    // the "Neue Abstimmung erstellen" section. Everyone else only sees sessions they're personally
    // part of the audience for (VoteEligibilityService.EvaluateAudienceAsync, same check used to
    // gate the results page) — a session a viewer has no rights to shouldn't even reveal its
    // existence to them.
    if (await channelAccessService.CanManageChannelAsync(principal, channelName, ct))
    {
        return Results.Ok(sessions);
    }

    var visibleSessions = new List<VoteSessionSummaryDto>();
    foreach (var session in sessions)
    {
        var eligibility = await voteEligibilityService.EvaluateAudienceAsync(principal, channelName, session.Id, ct);
        if (eligibility == VoteEligibilityResult.Allowed)
        {
            visibleSessions.Add(session);
        }
    }

    return Results.Ok(visibleSessions);
})
.RequireAuthorization();

app.MapGet("/api/channels/{channelName}/vote-sessions/{sessionId:long}/results", async (
    string channelName,
    long sessionId,
    HttpContext httpContext,
    IVoteSessionQueryService voteSessionQueryService,
    CancellationToken ct) =>
{
    // VoteAudienceFilter already required an authenticated, in-audience principal to reach this
    // handler — anonymous viewing was removed (see decision log). MyVote is filled in from the
    // now-guaranteed-present principal.
    var viewerTwitchUserId = httpContext.User.TryBuildTwitchPrincipal()!.TwitchUserId;
    var results = await voteSessionQueryService.GetResultsAsync(channelName, sessionId, viewerTwitchUserId, ct);
    return results is null ? Results.NotFound() : Results.Ok(results);
})
.RequireAuthorization()
.AddEndpointFilter<VoteAudienceFilter>();

app.MapPost("/api/channels/{channelName}/vote-sessions/{sessionId:long}/votes", async (
    string channelName,
    long sessionId,
    CastVoteRequest request,
    IVoteSessionService voteSessionService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(request.EmoteId))
    {
        return Results.BadRequest(new { error = "EmoteId must not be empty." });
    }

    if (request.Type is not (VoteType.Keep or VoteType.Delete))
    {
        return Results.BadRequest(new { error = "Type must be Keep (1) or Delete (2)." });
    }

    // VoteEligibilityFilter already required an authenticated principal to reach this handler.
    var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var (result, vote) = await voteSessionService.CastVoteAsync(channelName, sessionId, request.EmoteId, twitchUserId, request.Type, ct);

    return result switch
    {
        VoteCastResult.Success => Results.Ok(new { voteId = vote!.Id, emoteId = vote.EmoteId, type = vote.Type, updatedAt = vote.UpdatedAt }),
        VoteCastResult.ChannelNotFound => Results.NotFound(new { error = "Channel not found." }),
        VoteCastResult.SessionNotFound => Results.NotFound(new { error = "Vote session not found." }),
        VoteCastResult.SessionEnded => Results.Conflict(new { error = "Vote session has ended." }),
        VoteCastResult.EmoteNotEligible => Results.BadRequest(new { error = "Emote is unknown or archived." }),
        _ => Results.Problem()
    };
})
.RequireAuthorization()
.AddEndpointFilter<VoteEligibilityFilter>();

const string OAuthStateCookieName = "ep_oauth_state";
const string TwitchOAuthScope = "user:read:email user:read:moderated_channels user:read:subscriptions";

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

    var postLoginRedirectUrl = configuration["Auth:Twitch:PostLoginRedirectUrl"] ?? "/";
    return Results.Redirect(postLoginRedirectUrl);
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

app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

internal sealed record WorkerHealthPayload(bool IsConnected, DateTime? LastMessageReceivedUtc);
internal sealed record CreateVoteSessionRequest(string Title, AllowedRoles AllowedVoterRoles, DateTime? StartedAt = null);
internal sealed record CastVoteRequest(string EmoteId, VoteType Type);
internal sealed record SyncDeletedRequest(IReadOnlyList<string> EmoteIds);
