using System.Security.Claims;
using EmotePurge.Api.Endpoints;
using EmotePurge.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

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

// Applied only to endpoints that trigger expensive downstream work per call (Twitch IRC join,
// 7TV sync, DB writes) — not a blanket API-wide limit. Partitioned by authenticated user (all
// three endpoints require auth already), falling back to the remote IP just in case.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ExpensiveOps", httpContext =>
    {
        var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

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

// Security headers on every response. CSP allow-lists are deliberately narrow: connect-src covers
// the frontend's direct browser calls to 7TV (mass-delete GraphQL mutations bypass the API on
// purpose, s. CLAUDE.md "Zero-Knowledge für Schreib-Tokens"), img-src covers the 7TV CDN that
// serves emote preview images embedded via Emote.ImageUrl.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https://*.7tv.app https://7tv.io; " +
        "connect-src 'self' https://7tv.io; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";
    await next();
});

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapChannelEndpoints();
app.MapEmoteEndpoints();
app.MapUsageStatsEndpoints();
app.MapVoteSessionEndpoints();
app.MapAuthEndpoints();
app.MapWorkerHealthEndpoints();

app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
