using System.Security.Cryptography;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class UserServiceTests(PostgresFixture fixture)
{
    private static AesGcmTokenCipher CreateCipher()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Twitch:TokenEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();
        return new AesGcmTokenCipher(configuration);
    }

    [Fact]
    public async Task StoreTwitchTokens_ThenGet_RoundTripsAllFields()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());
        await service.UpsertLoginAsync("user-tokens-1", "usertokens1", "UserTokens1");
        var expiresAt = DateTime.UtcNow.AddHours(4);

        await service.StoreTwitchTokensAsync("user-tokens-1", "access-token", expiresAt, "refresh-token", "user:read:email");

        var tokens = await service.GetTwitchTokensAsync("user-tokens-1");
        Assert.NotNull(tokens);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        Assert.Equal("access-token", tokens.AccessToken);
        Assert.Equal(expiresAt, tokens.AccessTokenExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("user:read:email", tokens.Scopes);
    }

    [Fact]
    public async Task StoreTwitchTokens_NeverWritesPlaintextToTheDatabase()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());
        await service.UpsertLoginAsync("user-tokens-2", "usertokens2", "UserTokens2");

        await service.StoreTwitchTokensAsync("user-tokens-2", "plain-access", DateTime.UtcNow.AddHours(4), "plain-refresh", null);

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-tokens-2");
        Assert.NotNull(row.TwitchRefreshToken);
        Assert.NotNull(row.TwitchAccessToken);
        Assert.DoesNotContain("plain-refresh", row.TwitchRefreshToken);
        Assert.DoesNotContain("plain-access", row.TwitchAccessToken);
    }

    [Fact]
    public async Task GetTwitchTokens_WithDifferentCipherKey_ReturnsNull()
    {
        // Simulates a rotated/lost encryption key: the stored row exists but must behave exactly
        // like "no token stored" instead of surfacing garbage or an exception.
        await using var db = fixture.CreateDbContext();
        await new UserService(db, CreateCipher()).UpsertLoginAsync("user-tokens-3", "usertokens3", "UserTokens3");
        await new UserService(db, CreateCipher()).StoreTwitchTokensAsync(
            "user-tokens-3", "access", DateTime.UtcNow.AddHours(4), "refresh", null);

        await using var readDb = fixture.CreateDbContext();
        Assert.Null(await new UserService(readDb, CreateCipher()).GetTwitchTokensAsync("user-tokens-3"));
    }

    [Fact]
    public async Task ClearTwitchTokens_RemovesAllFourColumns()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());
        await service.UpsertLoginAsync("user-tokens-4", "usertokens4", "UserTokens4");
        await service.StoreTwitchTokensAsync("user-tokens-4", "access", DateTime.UtcNow.AddHours(4), "refresh", "scopes");

        await service.ClearTwitchTokensAsync("user-tokens-4");

        Assert.Null(await service.GetTwitchTokensAsync("user-tokens-4"));
        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-tokens-4");
        Assert.Null(row.TwitchRefreshToken);
        Assert.Null(row.TwitchAccessToken);
        Assert.Null(row.TwitchAccessTokenExpiresAtUtc);
        Assert.Null(row.TwitchTokenScopes);
    }

    [Fact]
    public async Task GetTwitchTokens_ForUnknownUser_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        Assert.Null(await new UserService(db, CreateCipher()).GetTwitchTokensAsync("user-tokens-nobody"));
    }

    [Fact]
    public async Task StoreTwitchTokens_ForUnknownUser_IsANoOp()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());

        await service.StoreTwitchTokensAsync("user-tokens-ghost", "access", DateTime.UtcNow, "refresh", null);

        Assert.Null(await service.GetTwitchTokensAsync("user-tokens-ghost"));
    }

    [Fact]
    public async Task RevokeSessions_WithActor_WritesTheAuditEntryInTheSameTransaction()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());
        await service.UpsertLoginAsync("user-revoke-1", "userrevoke1", "UserRevoke1");

        var revoked = await service.RevokeSessionsAsync("user-revoke-1", new AuditActor("admin-1", "sensitron"));

        Assert.True(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-revoke-1");
        Assert.NotNull(row.SessionsValidFromUtc);

        var entry = await verifyDb.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.TargetType == "user" && e.TargetId == "user-revoke-1");
        Assert.Equal(AuditActions.UserRevokeSessions, entry.Action);
        Assert.Equal("sensitron", entry.ActorLogin);
        Assert.Equal("admin-1", entry.ActorTwitchUserId);
        // The revoked user's login travels as a details snapshot — TargetId alone is just a number.
        Assert.Contains("userrevoke1", entry.DetailsJson);
    }

    [Fact]
    public async Task RevokeSessions_WithoutActor_RevokesButStaysUnaudited()
    {
        // The self-logout path: revocation happens, but no login/logout noise in the audit log.
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());
        await service.UpsertLoginAsync("user-revoke-2", "userrevoke2", "UserRevoke2");

        var revoked = await service.RevokeSessionsAsync("user-revoke-2", actor: null);

        Assert.True(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.NotNull((await verifyDb.Users.AsNoTracking().SingleAsync(u => u.Id == "user-revoke-2")).SessionsValidFromUtc);
        Assert.False(await verifyDb.AuditLogEntries.AsNoTracking().AnyAsync(e => e.TargetId == "user-revoke-2"));
    }

    [Fact]
    public async Task RevokeSessions_ForUnknownUser_ReturnsFalse_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var service = new UserService(db, CreateCipher());

        var revoked = await service.RevokeSessionsAsync("user-revoke-nobody", new AuditActor("admin-1", "sensitron"));

        Assert.False(revoked);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.AuditLogEntries.AsNoTracking().AnyAsync(e => e.TargetId == "user-revoke-nobody"));
    }
}
