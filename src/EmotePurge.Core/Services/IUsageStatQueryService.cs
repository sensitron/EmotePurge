namespace EmotePurge.Core.Services;

public record EmoteUsageDto(string EmoteName, DateOnly Date, int UseCount);

public record EmoteUsageTotalDto(string EmoteId, string EmoteName, string SevenTvEmoteId, string ImageUrl, int TotalUseCount);

public interface IUsageStatQueryService
{
    Task<IReadOnlyList<EmoteUsageDto>> GetUsageStatsAsync(string channelName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmoteUsageTotalDto>> GetUsageTotalsAsync(
        string channelName, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
