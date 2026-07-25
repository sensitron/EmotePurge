namespace EmotePurge.Core.Services;

public interface IModeratorCheckService
{
    Task<bool> IsModeratorAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);
}
