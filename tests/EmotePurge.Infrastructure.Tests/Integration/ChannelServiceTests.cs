using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class ChannelServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task JoinAsync_CreatesChannel_AndPublishesJoinCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var channel = await service.JoinAsync("ChannelServiceTest1");

        Assert.Equal("channelservicetest1", channel.ChannelName);
        Assert.True(channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "JOIN:channelservicetest1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_CalledTwice_WithoutLeaving_DoesNotCreateADuplicateRow()
    {
        // JoinAsync's null-check/else branch (reuse existing row, just flip IsBotActive) only
        // matters because the unique index on ChannelName would otherwise reject a second insert.
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var first = await service.JoinAsync("channelservicetest2");
        var second = await service.JoinAsync("channelservicetest2");

        Assert.Equal(first.Id, second.Id);
        var all = await service.ListAllAsync();
        Assert.Single(all, c => c.ChannelName == "channelservicetest2");
    }

    [Fact]
    public async Task LeaveAsync_RemovesChannel_AndPublishesLeaveCommand()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("channelservicetest3");

        var removed = await service.LeaveAsync("ChannelServiceTest3");

        Assert.True(removed);
        Assert.Null(await service.GetByNameAsync("channelservicetest3"));
        await redisPublisher.Received(1).PublishAsync("channel:bot:commands", "LEAVE:channelservicetest3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeaveAsync_ForUnknownChannel_ReturnsFalse_AndDoesNotPublish()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);

        var removed = await service.LeaveAsync("neverjoinedchannel");

        Assert.False(removed);
        await redisPublisher.DidNotReceive().PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByNameAsync_Normalizes_ChannelNameLookup()
    {
        await using var db = fixture.CreateDbContext();
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = new ChannelService(db, redisPublisher);
        await service.JoinAsync("ChannelServiceTest4");

        var found = await service.GetByNameAsync("  channelservicetest4  ");

        Assert.NotNull(found);
    }
}
