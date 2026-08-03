namespace EmotePurge.Core.Twitch;

// Process-wide cache around ITwitchAuthClient.GetAppAccessTokenAsync: an app access token lives
// ~60 days, so fetching one per poll tick would be pure waste. Deliberately not persisted — a
// fresh grant on process start is one cheap request, and Twitch invalidates the previous app
// token on each new grant anyway, so two processes sharing a stored token would fight over it.
public interface ITwitchAppTokenProvider
{
    // The cached token, or a freshly fetched one when none is cached or the cached one is close
    // to expiry. Null = Twitch could not be reached or refused (already logged); callers skip
    // their tick and retry on the next one.
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    // Drops the cached token so the next GetTokenAsync fetches fresh. Called when a Helix request
    // with the token failed — e.g. after a client-secret rotation revoked it mid-lifetime.
    void Invalidate();
}
