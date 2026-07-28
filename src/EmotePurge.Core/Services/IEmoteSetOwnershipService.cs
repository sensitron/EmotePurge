namespace EmotePurge.Core.Services;

public record EmoteSetWarningDto(
    bool Available,
    bool IsOwnSet,
    IReadOnlyList<string> OtherTrackedChannelsSharingSet,
    IReadOnlyList<string> OtherModeratedChannelsSharingSet);

public interface IEmoteSetOwnershipService
{
    Task<EmoteSetWarningDto> CheckAsync(
        string channelName,
        string? callerTwitchUserId,
        string? callerAccessToken,
        CancellationToken cancellationToken = default);
}
