using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class DuplicateEmoteNameQueryServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetAsync_ReturnsCollidingNames_WithAllInvolvedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dupquery1",
            ("7tv-b", "Dup", false),
            ("7tv-a", "Dup", false),
            ("7tv-c", "Solo", false));

        var duplicates = await new DuplicateEmoteNameQueryService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(duplicates);
        var group = Assert.Single(duplicates);
        Assert.Equal("Dup", group.Name);
        // Ordered by SevenTvEmoteId (ULIDs sort chronologically), not by insertion order.
        Assert.Equal(["7tv-a", "7tv-b"], group.Emotes.Select(e => e.SevenTvEmoteId));
        Assert.All(group.Emotes, e => Assert.False(string.IsNullOrEmpty(e.EmoteId)));
        Assert.All(group.Emotes, e => Assert.False(string.IsNullOrEmpty(e.ImageUrl)));
    }

    [Fact]
    public async Task GetAsync_IgnoresArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dupquery2",
            ("7tv-a", "Dup", false),
            ("7tv-b", "Dup", true));

        var duplicates = await new DuplicateEmoteNameQueryService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(duplicates);
        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task GetAsync_TreatsCasingVariantsAsDistinctNames()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dupquery3",
            ("7tv-a", "Emote", false),
            ("7tv-b", "emote", false));

        var duplicates = await new DuplicateEmoteNameQueryService(db).GetAsync(channel.ChannelName);

        Assert.NotNull(duplicates);
        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task GetAsync_NormalizesTheChannelName()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "dupquery4",
            ("7tv-a", "Dup", false),
            ("7tv-b", "Dup", false));

        var duplicates = await new DuplicateEmoteNameQueryService(db).GetAsync("  DupQuery4 ");

        Assert.NotNull(duplicates);
        Assert.Single(duplicates);
    }

    [Fact]
    public async Task GetAsync_UntrackedChannel_ReturnsNull()
    {
        await using var db = fixture.CreateDbContext();

        var duplicates = await new DuplicateEmoteNameQueryService(db).GetAsync("dupquery5");

        Assert.Null(duplicates);
    }

    private static async Task<Channel> SeedChannelAsync(
        Persistence.AppDbContext db,
        string name,
        params (string SevenTvId, string Name, bool Archived)[] emotes)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}" };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, emoteName, archived) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = emoteName,
                ImageUrl = $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp",
                IsArchived = archived
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }
}
