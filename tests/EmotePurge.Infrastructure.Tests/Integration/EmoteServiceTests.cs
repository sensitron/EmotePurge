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
        // Drives the channel.synced live event in the endpoint: this call really changed state.
        Assert.Equal(1, result.NewlyArchivedCount);
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
        // Goal state was already reached, so nothing was written — no live event, no audit row.
        Assert.Equal(0, result.NewlyArchivedCount);
    }

    [Fact]
    public async Task MarkDeletedAsync_StampsArchivedAt_ForNewlyArchivedEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "syncdeletetest_d", TwitchChannelId = "4005", ActiveEmoteSetId = "set-d" };
        var emote = new Emote { ChannelId = channel.Id, Channel = channel, Name = "Stamp", SevenTvEmoteId = "7tv-d1", ImageUrl = "https://cdn/d1" };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var service = new EmoteService(db);
        await service.MarkDeletedAsync("syncdeletetest_d", [emote.Id], Actor);

        var archivedAt = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task MarkDeletedAsync_LeavesTheArchiveDateOfAnAlreadyArchivedEmoteAlone()
    {
        // The live sync usually archives first (with the accurate timestamp); this later
        // bookkeeping call counts the row as archived but must not overwrite the earlier date.
        await using var db = fixture.CreateDbContext();
        var earlier = DateTime.UtcNow.AddMinutes(-10);
        var channel = new Channel { ChannelName = "syncdeletetest_e", TwitchChannelId = "4006", ActiveEmoteSetId = "set-e" };
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Channel = channel,
            Name = "Kept",
            SevenTvEmoteId = "7tv-e1",
            ImageUrl = "https://cdn/e1",
            IsArchived = true,
            ArchivedAt = earlier
        };
        db.Channels.Add(channel);
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();

        var service = new EmoteService(db);
        await service.MarkDeletedAsync("syncdeletetest_e", [emote.Id], Actor);

        var archivedAt = await db.Emotes.Where(e => e.Id == emote.Id).Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.Equal(earlier, archivedAt.Value, TimeSpan.FromSeconds(1));
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
        Assert.Equal(0, result.NewlyArchivedCount);
        Assert.Equal(2, result.NotFoundIds.Count);
        // The foreign channel's emote stays untouched.
        Assert.False(await db.Emotes.Where(e => e.Id == foreignEmote.Id).Select(e => e.IsArchived).SingleAsync());
    }
}
