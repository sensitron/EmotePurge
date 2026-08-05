using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Review 2026-07-29, open question 17: no API response used to set <c>Cache-Control</c>, leaving
/// caching behavior up to whatever proxy sits in front. <c>no-store</c> on everything under
/// <c>/api/</c> closes that — API answers are per-user, cookie-authenticated data that must never
/// be served from a shared cache.
/// </summary>
public class ApiCacheControlTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiCacheControlTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ApiResponses_CarryCacheControlNoStore()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/worker/health");

        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store fehlt auf einer /api/-Antwort.");
    }

    [Fact]
    public async Task ApiFallback404_CarriesCacheControlNoStore()
    {
        using var client = _factory.CreateClient();

        // The /api/{**rest} fallback is the easiest response to cache by accident — it bypasses
        // every endpoint group and its filters.
        var response = await client.GetAsync("/api/does-not-exist");

        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store fehlt auf dem /api/-Fallback.");
    }
}
