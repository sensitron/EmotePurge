namespace EmotePurge.Core.SevenTv;

public record SevenTvEmote(string Id, string Name, string ImageUrl);

public record SevenTvEmoteSet(string Id, IReadOnlyList<SevenTvEmote> Emotes);
