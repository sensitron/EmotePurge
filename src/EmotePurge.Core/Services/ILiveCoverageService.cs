namespace EmotePurge.Core.Services;

public interface ILiveCoverageService
{
    /// <summary>
    /// Credits <paramref name="minutes"/> of live coverage to <paramref name="dateUtc"/> for every
    /// named channel — one call per poll tick of the worker's Helix streams poll. Names are
    /// normalized; names without a channel row are skipped silently (the channel may have been
    /// purged between listing and poll). A day is clamped to 1440 minutes, so an occasional extra
    /// poll (worker restart) cannot push a day past its own length. Returns the number of channels
    /// actually credited.
    /// </summary>
    Task<int> AddLiveMinutesAsync(
        IReadOnlyCollection<string> liveChannelNames, DateOnly dateUtc, int minutes, CancellationToken cancellationToken = default);
}
