export interface ChannelStatus {
  channelId: string;
  channelName: string;
  isBotActive: boolean;
  activeEmoteSetId: string;
}

export interface AdminChannelDto {
  channelId: string;
  channelName: string;
  isBotActive: boolean;
  twitchChannelId: string | null;
  createdAt: string;
}

// Independent flags, not a single role — a channel can be broadcaster-self, Twitch-moderator,
// 7TV-editor, any combination, or (7TV-editor-only) none of the Twitch roles at all.
export interface MyChannelDto {
  channelName: string;
  isBroadcaster: boolean;
  isModerator: boolean;
  isSevenTvEditor: boolean;
  isTracked: boolean;
  isBotActive: boolean;
}

export interface MyChannelsResult {
  helixUnavailable: boolean;
  sevenTvUnavailable: boolean;
  channels: MyChannelDto[];
}
