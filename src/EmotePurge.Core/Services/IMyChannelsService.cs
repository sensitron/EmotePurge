namespace EmotePurge.Core.Services;

public enum ChannelRole
{
    Broadcaster = 1,
    Moderator = 2
}

public record MyChannelDto(string ChannelName, ChannelRole Role, bool IsTracked, bool IsBotActive);

// HelixUnavailable=true means the moderated-channels part of the list couldn't be refreshed
// (expired/missing access token, or a transient Helix failure) — Channels then only contains
// the broadcaster-self entry, not a full picture; the frontend should say so, not treat it as "no channels".
public record MyChannelsResultDto(bool HelixUnavailable, IReadOnlyList<MyChannelDto> Channels);

public interface IMyChannelsService
{
    Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default);
}
