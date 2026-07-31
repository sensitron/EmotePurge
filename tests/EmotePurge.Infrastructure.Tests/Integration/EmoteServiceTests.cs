using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("100", "synctester");

    [Fact]
    public async Task MarkDeletedAsync_ArchivesActiveEmotes_AndReportsThemAsArchived()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_a", TwitchChannelId = "4001", ActiveEmoteSetId = "set-a" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "PogU", SevenTvEmoteId = "7tv-a1", ImageUrl = "https://cdn/a1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_a", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Empty(result.NotFoundIds);
        Assert.True(await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task MarkDeletedAsync_CountsAlreadyArchivedEmoteAsArchived()
    {
        // The realistic race since the EventAPI live sync: the worker archives the emote off the
        // 7TV dispatch seconds before the frontend's bookkeeping call arrives. That call must see
        // "goal state reached", not "not found" — the old behavior made every successful delete
        // look like a failed sync in the UI.
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_b", TwitchChannelId = "4002", ActiveEmoteSetId = "set-b" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "KEKW", SevenTvEmoteId = "7tv-b1", ImageUrl = "https://cdn/b1", IsArchived = true };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_b", [emote.Id], Actor);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Empty(result.NotFoundIds);
    }

    [Fact]
    public async Task MarkDeletedAsync_ReportsUnknownAndForeignIdsAsNotFound()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_c", TwitchChannelId = "4003", ActiveEmoteSetId = "set-c" };
        var foreignChannel = new Channel { ChannelName = "syncdeletetest_c2", TwitchChannelId = "4004", ActiveEmoteSetId = "set-c2" };
        var foreignEmote = new Emote { ChannelId = foreignChannel.Id, Channel = foreignChannel, Name = "Foreign", SevenTvEmoteId = "7tv-c1", ImageUrl = "https://cdn/c1" };
        db.Channels.AddRange(channel, foreignChannel);
        db.Emotes.Add(foreignEmote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db);
        var result = await service.MarkDeletedAsync("syncdeletetest_c", [foreignEmote.Id, "does-not-exist"], Actor);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays untouched.
        Assert.False(await db.Emotes.Where(e => e.Id == foreignEmote.Id).Select(e => e.IsArchived).SingleAsync());
    }
}
