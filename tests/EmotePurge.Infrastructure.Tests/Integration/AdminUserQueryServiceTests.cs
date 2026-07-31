using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The Postgres collection shares one database across test classes (UserServiceTests etc. seed
/// their own users), so these tests pin their rows to page 1 by seeding far-future LastLogin values
/// and assert on their own id prefix only — same discipline as AuditLogQueryServiceTests.
/// </summary>
[Collection("Postgres")]
public class AdminUserQueryServiceTests(PostgresFixture fixture)
{
    private const string IdPrefix = "adminuserquery";

    [Fact]
    public async Task ListAsync_OrdersByLastLoginDescending()
    {
        await using var db = fixture.CreateDbContext();
        db.Users.AddRange(
            NewUser($"{IdPrefix}-order-a", lastLogin: new DateTime(2099, 7, 29, 10, 0, 0, DateTimeKind.Utc)),
            NewUser($"{IdPrefix}-order-b", lastLogin: new DateTime(2099, 7, 31, 10, 0, 0, DateTimeKind.Utc)),
            NewUser($"{IdPrefix}-order-c", lastLogin: new DateTime(2099, 7, 30, 10, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var page = await new AdminUserQueryService(db).ListAsync(1, 100);

        var ids = page.Items.Where(u => u.TwitchUserId.StartsWith($"{IdPrefix}-order")).Select(u => u.TwitchUserId).ToList();
        Assert.Equal([$"{IdPrefix}-order-b", $"{IdPrefix}-order-c", $"{IdPrefix}-order-a"], ids);
    }

    [Fact]
    public async Task ListAsync_DerivesHasRefreshToken_AndNeverExposesTheCiphertext()
    {
        await using var db = fixture.CreateDbContext();
        var withToken = NewUser($"{IdPrefix}-token-yes", lastLogin: new DateTime(2099, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        withToken.TwitchRefreshToken = "opaque-ciphertext";
        withToken.TwitchAccessToken = "opaque-ciphertext-2";
        withToken.TwitchAccessTokenExpiresAtUtc = new DateTime(2099, 8, 1, 14, 0, 0, DateTimeKind.Utc);
        withToken.TwitchTokenScopes = "user:read:email";
        var withoutToken = NewUser($"{IdPrefix}-token-no", lastLogin: new DateTime(2099, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        db.Users.AddRange(withToken, withoutToken);
        await db.SaveChangesAsync();

        var page = await new AdminUserQueryService(db).ListAsync(1, 100);

        var yes = Assert.Single(page.Items, u => u.TwitchUserId == $"{IdPrefix}-token-yes");
        Assert.True(yes.HasRefreshToken);
        Assert.Equal(withToken.TwitchAccessTokenExpiresAtUtc, yes.TwitchAccessTokenExpiresAtUtc);
        Assert.Equal("user:read:email", yes.TwitchTokenScopes);

        var no = Assert.Single(page.Items, u => u.TwitchUserId == $"{IdPrefix}-token-no");
        Assert.False(no.HasRefreshToken);
        // No token field exists on the DTO to assert against — that absence is the contract; this
        // test pins the boolean derivation working against real encrypted-looking column content.
    }

    [Fact]
    public async Task ListAsync_PagesWithoutOverlap_BreakingLastLoginTiesById()
    {
        await using var db = fixture.CreateDbContext();
        var sameInstant = new DateTime(2099, 8, 2, 10, 0, 0, DateTimeKind.Utc);
        db.Users.AddRange(Enumerable.Range(0, 5).Select(i => NewUser($"{IdPrefix}-paging-{i}", lastLogin: sameInstant)));
        await db.SaveChangesAsync();

        var service = new AdminUserQueryService(db);
        var first = await service.ListAsync(1, 2);
        var second = await service.ListAsync(2, 2);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.TotalCount >= 5);
        Assert.Empty(first.Items.Select(u => u.TwitchUserId).Intersect(second.Items.Select(u => u.TwitchUserId)));
        // Shared timestamp: the Id tiebreaker alone decides the order, ascending and stable.
        Assert.Equal([$"{IdPrefix}-paging-0", $"{IdPrefix}-paging-1"], first.Items.Select(u => u.TwitchUserId));
    }

    private static User NewUser(string id, DateTime lastLogin)
        => new()
        {
            Id = id,
            TwitchUsername = id.Replace("-", ""),
            DisplayName = id,
            LastLogin = lastLogin
        };
}
