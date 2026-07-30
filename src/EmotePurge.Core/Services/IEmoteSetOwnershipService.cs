namespace EmotePurge.Core.Services;

public record EmoteSetWarningDto(
    bool Available,
    bool IsOwnSet,
    IReadOnlyList<string> OtherTrackedChannelsSharingSet,
    IReadOnlyList<string> OtherModeratedChannelsSharingSet);

public interface IEmoteSetOwnershipService
{
    // caller is null for anonymous requests — the moderated-channels tier is then skipped.
    Task<EmoteSetWarningDto> CheckAsync(
        string channelName,
        TwitchPrincipalInfo? caller,
        CancellationToken cancellationToken = default);
}
