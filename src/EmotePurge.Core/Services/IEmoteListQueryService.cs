namespace EmotePurge.Core.Services;

/// <summary>An active emote, reduced to the two fields a cross-channel comparison needs.</summary>
/// <remarks>
/// Deliberately no <c>Emote.Id</c>: that guid is channel-scoped (rule 8) and would be meaningless
/// when this list is compared against emotes from another channel, which is the whole point of an
/// import.
/// </remarks>
public record EmoteListItemDto(string SevenTvEmoteId, string Name);

public interface IEmoteListQueryService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<IReadOnlyList<EmoteListItemDto>?> ListActiveAsync(string channelName, CancellationToken cancellationToken = default);
}
