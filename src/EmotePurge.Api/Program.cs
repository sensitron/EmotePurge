using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Endpoints;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);

// Without a shared key ring every container restart invalidates every auth cookie, and two API
// replicas would reject each other's logins outright. Only enabled when a path is configured
// (docker-compose sets DataProtection__KeyPath=/keys onto a named volume) — local `dotnet run`
// keeps the default per-user key store, which already persists.
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
        .SetApplicationName("EmotePurge");
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // Secure regardless of what the proxy sends. The default SameAsRequest derives the flag from
        // Request.IsHttps, which in this topology exists *only* because ForwardedHeadersMiddleware
        // saw an X-Forwarded-Proto that the app cannot guarantee: a replaced reverse proxy or a new
        // vhost missing `proxy_set_header X-Forwarded-Proto` would silently start handing out a
        // cookie without Secure — carrying the Twitch access token and all of the victim's
        // broadcaster/mod rights. Fail closed: a missing header now breaks login visibly instead of
        // working insecurely. Browsers treat http://localhost as trustworthy, so dev is unaffected.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;

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

        // Server-side revocation. Costs one primary-key lookup per authenticated request, which buys
        // the only way to invalidate an issued cookie: before this, logout deleted the browser's
        // copy while the cookie itself stayed valid for its full 14 days.
        options.Events.OnValidatePrincipal = async context =>
        {
            var twitchUserId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var issuedAtRaw = context.Principal?.FindFirstValue(TwitchClaimTypes.SessionIssuedAtUtc);

            // A cookie without the claim predates session tracking. Rejected rather than
            // grandfathered in — accepting it would leave a permanent bypass of the check.
            if (twitchUserId is null
                || !DateTime.TryParse(issuedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issuedAt))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
            var validFrom = await userService.GetSessionsValidFromUtcAsync(twitchUserId, context.HttpContext.RequestAborted);
            if (validFrom is { } revokedBefore && issuedAt.ToUniversalTime() < revokedBefore)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();

// Two policies rather than one, because "expensive" meant two unrelated things. Both partition by
// authenticated user (every endpoint carrying them requires auth), falling back to the remote IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict: every endpoint under this policy makes uncached calls to Twitch Helix or 7TV, and
    // those quotas are per application, not per user. A single looping account could therefore
    // exhaust the app-wide bucket, at which point Helix returns nothing for *everyone* and
    // ModeratorCheckService can no longer distinguish "not a mod" from "quota exhausted" — every
    // moderator of every channel silently loses their permissions.
    // 40, not 20: ordinary navigation burns several permits per page switch (/channels/mine on
    // every overview visit, /permissions plus usage-stats on every workspace entry), so 20/min
    // ran out after ~7 page switches of plain clicking. The worst case per account stays below
    // the app-wide Helix bucket (~800/min) even at /mine's up-to-10 Helix calls per request.
    options.AddPolicy("ExternalApi", httpContext => PartitionPerUser(httpContext, permitLimit: 40));

    // Generous: bookkeeping against our own database with no downstream cost. Deliberately split
    // out of the strict policy — sync-deleted is the one call that must never be dropped (a 429
    // there leaves the database diverging from 7TV with no signal), and it used to share the
    // 20/min budget with join and the vote endpoints.
    options.AddPolicy("Bookkeeping", httpContext => PartitionPerUser(httpContext, permitLimit: 120));

    // Stricter than ExternalApi by a factor of eight, for the one endpoint a user can trigger that
    // costs an unconditional 7TV call *and* fans a live event out to every open page of the channel.
    // It is only half the guard: this partitions per user, so fifteen moderators of one channel
    // would still get fifteen budgets — the per-channel half is IChannelResyncCooldown. Neither
    // mechanism covers the other's case.
    options.AddPolicy("ChannelResync", httpContext => PartitionPerUser(httpContext, permitLimit: 5));

    // For the payload-free GET /api/health: public and anonymous, so this always partitions by IP
    // (PartitionPerUser falls back to it). One Redis read per hit — cheap, but unauthenticated,
    // and its legitimate callers are machines on fixed cadences: the container HEALTHCHECK
    // (every 30 s, from localhost) and the external uptime monitor (every 60 s).
    options.AddPolicy("PublicHealth", httpContext => PartitionPerUser(httpContext, permitLimit: 30));
});

static RateLimitPartition<string> PartitionPerUser(HttpContext httpContext, int permitLimit)
{
    var partitionKey = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? httpContext.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Without this an unhandled exception aborts the response body-less, so the frontend cannot tell a
// crash apart from a network failure and shows its generic message. Concretely reachable: two
// concurrent votes racing the (VoteSessionId, EmoteId, UserId) unique index. Deliberately no
// exception detail in the body — only a stable errorCode the frontend can translate.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { errorCode = ApiErrorCodes.UnexpectedError });
}));

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

// Deliberately no UseHttpsRedirection(): Kestrel only listens on http://+:8080 inside the container
// and no ASPNETCORE_HTTPS_PORT is set, so it was a no-op that merely suggested protection it never
// provided. TLS is terminated by the host reverse proxy; HSTS below is what actually enforces it.

// Security headers on every response. CSP allow-lists are deliberately narrow: connect-src covers
// the frontend's direct browser calls to 7TV (mass-delete GraphQL mutations bypass the API on
// purpose, s. CLAUDE.md "Zero-Knowledge für Schreib-Tokens"), img-src covers the 7TV CDN that
// serves emote preview images embedded via Emote.ImageUrl.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Review open question 17: API responses are per-user, cookie-authenticated data and must
    // never be served from a shared cache — the API itself never sent any Cache-Control, leaving
    // the decision to whatever proxy sits in front. Static assets are unaffected: their headers
    // are set later by ApplyStaticCacheHeaders, and they never live under /api/.
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Set as a plain response header rather than via app.UseHsts(), which takes the same unreliable
    // Request.IsHttps detour as the cookie flag did. Without HSTS, a user typing "emotepurge.app"
    // without a scheme makes the first request over http:// — enough for an sslstrip attacker on an
    // open network to read the session cookie once. Browsers ignore this header over plain http, so
    // local development is unaffected. Deliberately no includeSubDomains and no preload: both are
    // effectively irreversible for a year once cached, and would also bind subdomains this app knows
    // nothing about. Adding them later is safe; removing them is not.
    headers["Strict-Transport-Security"] = "max-age=31536000";
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

// Angular fingerprints its bundles — eight hash characters before the extension, in two different
// alphabets depending on whether esbuild emitted an entry point (main-4HRH6XFH.js) or a lazy chunk
// (chunk-8nP_m4am.js). A pattern covering only one of the two would leave the other uncached.
// Deliberately restricted to .js/.css: favicon.ico, robots.txt and the i18n JSONs sit right next to
// them in wwwroot with stable, unhashed names and must never be frozen for a year.
var fingerprintedAsset = new Regex(@"-[A-Za-z0-9_-]{8}\.(js|css)$", RegexOptions.Compiled);

// Without an explicit Cache-Control, ASP.NET Core sends only ETag/Last-Modified, and browsers then
// apply heuristic freshness — reusing a file for a fraction of its age without revalidating at all.
// That shipped stale translations to production on 2026-07-30 (/i18n/de.json has a stable URL, so a
// cached copy simply lacked the newly added keys and Transloco rendered the raw key names). A stale
// index.html is the more serious case: it references hashed bundles that no longer exist after a
// redeploy, and since we send X-Content-Type-Options: nosniff, the SPA fallback's text/html response
// for those bundles is refused outright rather than misparsed — a blank page instead of a stale one.
// "no-cache" does not forbid caching; it requires revalidation, which the existing ETag answers with
// an empty 304.
void ApplyStaticCacheHeaders(StaticFileResponseContext context)
{
    var path = context.Context.Request.Path.Value ?? string.Empty;
    context.Context.Response.Headers.CacheControl = fingerprintedAsset.IsMatch(path)
        ? "public, max-age=31536000, immutable"
        : "no-cache";
}

app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ApplyStaticCacheHeaders });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapChannelEndpoints();
app.MapEmoteEndpoints();
app.MapUsageStatsEndpoints();
app.MapVoteSessionEndpoints();
app.MapAuthEndpoints();
app.MapWorkerHealthEndpoints();
app.MapAdminEndpoints();
app.MapLiveEndpoints();

app.MapFallback("/api/{**rest}", () => Results.NotFound());
// Needs the options passed separately: the SPA fallback serves index.html through its own endpoint,
// not through the static-file middleware configured above, so it would otherwise stay uncontrolled —
// which is exactly the file that must not go stale.
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = ApplyStaticCacheHeaders });

// S3-34 fail-fast: migrations are applied manually in this project, and a container running
// against a database that is missing one used to start "healthy" and answer with silent 500s.
// Crashing here makes the forgotten migration visible as a restart loop instead.
await using (var migrationScope = app.Services.CreateAsyncScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<IPendingMigrationGuard>()
        .EnsureNoPendingMigrationsAsync();
}

app.Run();

// Top-level statements compile into an internal Program class, which WebApplicationFactory<T>
// cannot reach. Declaring the partial publicly is the documented way to make the real pipeline —
// middleware order, filter registration and all — testable from tests/EmotePurge.Api.Tests without
// duplicating any of it there.
public partial class Program;
