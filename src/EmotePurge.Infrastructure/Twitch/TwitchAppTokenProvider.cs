using EmotePurge.Core.Twitch;
using Microsoft.Extensions.DependencyInjection;

namespace EmotePurge.Infrastructure.Twitch;

// Singleton (one token per process); resolves ITwitchAuthClient through a scope per fetch, because
// typed HttpClients are transient and capturing one here would pin its message handler for the
// process lifetime. Single-flight via semaphore: concurrent callers during a fetch wait for the
// one request instead of each grabbing their own grant. That is not about grants revoking one
// another — measured 2026-08-31 against the real Twitch API (dev client ID): two client_credentials
// grants in a row return two distinct tokens, and the older one keeps working afterwards
// (GET /oauth2/validate and a real Helix call both answer 200 for it). Twitch's docs are silent on
// this either way. What the gate actually buys is avoiding needless token churn: two 2020 Twitch
// dev-forum posts (not from Twitch staff, not officially confirmed) describe an undocumented cap of
// roughly 25 concurrently valid app tokens per client ID, with the oldest evicted past that — reason
// enough to not mint a fresh grant per caller, even though this codebase has never hit that limit
// directly.
public class TwitchAppTokenProvider(IServiceScopeFactory scopeFactory) : ITwitchAppTokenProvider
{
    // Generous because the token lives ~60 days: refreshing an hour early costs one request per
    // two months, while a token that expires mid-tick costs a failed Helix batch.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private TwitchTokenResult? _cached;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (IsUsable(cached))
        {
            return cached!.AccessToken;
        }

        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            if (IsUsable(_cached))
            {
                return _cached!.AccessToken;
            }

            using var scope = scopeFactory.CreateScope();
            var authClient = scope.ServiceProvider.GetRequiredService<ITwitchAuthClient>();
            var token = await authClient.GetAppAccessTokenAsync(cancellationToken);
            if (token is null)
            {
                // Failure already logged by the client; deliberately not cached — the next caller
                // retries.
                return null;
            }

            _cached = token;
            return token.AccessToken;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    private static bool IsUsable(TwitchTokenResult? token) =>
        token is not null && token.ExpiresAtUtc - DateTime.UtcNow > ExpiryMargin;
}
