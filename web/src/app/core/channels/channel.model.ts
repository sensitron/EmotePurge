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

// Matches EmotePurge.Core.Services.ChannelRole (numeric — no JsonStringEnumConverter registered).
export enum ChannelRole {
  Broadcaster = 1,
  Moderator = 2,
}

export interface MyChannelDto {
  channelName: string;
  role: ChannelRole;
  isTracked: boolean;
  isBotActive: boolean;
}

export interface MyChannelsResult {
  helixUnavailable: boolean;
  channels: MyChannelDto[];
}
