namespace EmotePurge.Core.Services;

public record EmoteUsageDto(string EmoteName, DateTime Date, int UseCount);

public interface IUsageStatQueryService
{
    Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default);
}
