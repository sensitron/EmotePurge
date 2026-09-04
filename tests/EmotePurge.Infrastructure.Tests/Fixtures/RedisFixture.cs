using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

// One real redis:7.2-alpine container (same image as docker-compose.yml) shared across all
// tests in the "Redis" collection.
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.2-alpine")
        .Build();

    private IConnectionMultiplexer? _connection;

    public IConnectionMultiplexer Connection => _connection
        ?? throw new InvalidOperationException("RedisFixture has not been initialized yet.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
