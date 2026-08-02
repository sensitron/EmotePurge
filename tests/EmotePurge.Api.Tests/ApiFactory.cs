using System.Security.Claims;
using System.Text.Encodings.Web;
using EmotePurge.Api.Auth;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Boots the real <c>Program.cs</c> pipeline — same middleware order, same <c>MapGroup</c> tree,
/// same filter registrations — with three substitutions and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The two authorization services are substituted because they are what the filters ask; the tests
/// then drive the filter matrix by deciding what those services answer. The database and Redis are
/// never resolved: every assertion below is reached by a filter short-circuiting before the handler,
/// and the one allow-path test substitutes the single service its handler uses.
/// </para>
/// <para>
/// The cookie scheme is replaced rather than driven, because it cannot be driven from a test:
/// <c>CookieSecurePolicy.Always</c> means <c>HttpClient</c> never stores the cookie over the
/// TestServer's <c>http://localhost</c> base address, and <c>OnValidatePrincipal</c> would hit
/// Postgres on every authenticated request. The replacement keeps the status codes identical —
/// a challenge is still 401 and a forbid is still 403.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IChannelAccessService ChannelAccess { get; } = Substitute.For<IChannelAccessService>();

    public IVoteEligibilityService VoteEligibility { get; } = Substitute.For<IVoteEligibilityService>();

    public IChannelService Channels { get; } = Substitute.For<IChannelService>();

    /// <summary>
    /// Substituted explicitly rather than left to the real Redis-backed implementation: the resync
    /// handler consults it, and per the note below every handler service is constructed before the
    /// filter pipeline runs — so even a 403 case would otherwise reach for a Redis that is not
    /// there. It also lets a test drive the cooldown outcome directly.
    /// </summary>
    public IChannelResyncCooldown ResyncCooldown { get; } = Substitute.For<IChannelResyncCooldown>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" rather than the default "Development": Development would load the developer's
        // user-secrets (the API project has a UserSecretsId), which makes the suite depend on the
        // machine it runs on. Redis:ConnectionString has to be present or AddEmotePurgeInfrastructure
        // throws at startup — present, not reachable: nothing here ever opens the connection.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Redis:ConnectionString", "localhost:6379");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=none;Username=none;Password=none");
        builder.UseSetting("Auth:AdminTwitchLogins", string.Empty);

        // Warnings and above still reach the console. A failing filter test reports only a status
        // code, and the difference between "the filter answered 500" and "the filter answered the
        // wrong code" is otherwise invisible — the unhandled-exception log is what tells them apart.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped(_ => ChannelAccess);
            services.AddScoped(_ => VoteEligibility);
            services.AddScoped(_ => Channels);
            services.AddSingleton(_ => ResyncCooldown);

            // Load-bearing, and not obvious: RequestDelegateFactory resolves a handler's injected
            // services *before* it runs the endpoint filter pipeline. A request the filter is about
            // to reject with 403 therefore still constructs every service that handler asks for —
            // and several of those take IConnectionMultiplexer, whose registration calls
            // ConnectionMultiplexer.Connect eagerly. Without this line the matrix spends twelve
            // seconds per case waiting for a Redis that is not there and then answers 500 instead of
            // the status code under test. Substituting the multiplexer keeps the whole suite free of
            // external infrastructure while leaving the real service graph in place.
            services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

            // Registered after the cookie scheme, so this DefaultScheme assignment is the one that
            // sticks. Forbid() and the RequireAuthorization challenge both resolve to this handler.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}

/// <summary>
/// Turns two request headers into a principal. Absent header = anonymous, which is how the 401 cases
/// are driven; a user id without a login is the "authenticated but claims incomplete" case that
/// <c>TryBuildTwitchPrincipal</c> rejects, and which every filter answers with 401 rather than 403.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserIdHeader = "X-Test-UserId";
    public const string LoginHeader = "X-Test-Login";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (Request.Headers.TryGetValue(LoginHeader, out var login))
        {
            claims.Add(new Claim(TwitchClaimTypes.Login, login.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
