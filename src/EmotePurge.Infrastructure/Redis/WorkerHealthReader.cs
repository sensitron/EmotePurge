using System.Text.Json;
using EmotePurge.Core.Services;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure.Redis;

public class WorkerHealthReader(IConnectionMultiplexer connectionMultiplexer) : IWorkerHealthReader
{
    /// <summary>
    /// The one place this key is named. The worker writes the same constant through
    /// <see cref="WorkerHealthKeys"/>; the API no longer knows it at all.
    /// </summary>
    public async Task<WorkerHealthSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var value = await connectionMultiplexer.GetDatabase().StringGetAsync(WorkerHealthKeys.TwitchConnection);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkerHealthSnapshot>((string)value!, JsonSerializerOptions.Web);
    }
}

/// <summary>
/// Shared between the worker (writer) and this reader. Lives in Infrastructure because both projects
/// reference it, while they do not reference each other.
/// </summary>
public static class WorkerHealthKeys
{
    public const string TwitchConnection = "worker:health:twitch";

    /// <summary>
    /// Three times the worker's 20-second publish interval, so a single missed write does not read as
    /// a dead worker.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
}
