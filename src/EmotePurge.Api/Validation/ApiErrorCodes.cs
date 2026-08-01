namespace EmotePurge.Api.Validation;

/// <summary>
/// Stable, language-neutral error identifiers returned in API error bodies as
/// <c>{ errorCode = ApiErrorCodes.X }</c>. The frontend owns the actual display text (translated
/// per the active language) — the API never returns human-readable prose, so there is exactly one
/// place (the frontend's translation files) that needs updating per supported language, not two.
/// </summary>
internal static class ApiErrorCodes
{
    public const string InvalidChannelName = "invalid_channel_name";
    public const string InvalidChannelOrSessionId = "invalid_channel_or_session_id";
    public const string EmoteIdsEmpty = "emote_ids_empty";
    public const string EmoteIdsInvalid = "emote_ids_invalid";
    public const string InvalidDateFormat = "invalid_date_format";
    public const string FromAfterTo = "from_after_to";
    public const string RangeTooLarge = "range_too_large";
    public const string InvalidOAuthState = "invalid_oauth_state";
    public const string TwitchTokenExchangeFailed = "twitch_token_exchange_failed";
    public const string TwitchUserInfoUnavailable = "twitch_user_info_unavailable";
    public const string VoteSessionTitleEmpty = "vote_session_title_empty";
    public const string VoteSessionRolesEmpty = "vote_session_roles_empty";
    public const string VipsNotSupported = "vips_not_supported";
    public const string StartedAtInFuture = "started_at_in_future";
    public const string ChannelNotJoined = "channel_not_joined";
    public const string EmoteIdEmpty = "emote_id_empty";
    public const string InvalidVoteType = "invalid_vote_type";
    public const string ChannelNotFound = "channel_not_found";
    public const string VoteSessionNotFound = "vote_session_not_found";
    public const string VoteSessionEnded = "vote_session_ended";
    public const string EmoteNotEligible = "emote_not_eligible";
    // Returned by the global exception handler — deliberately opaque, no exception detail.
    public const string UnexpectedError = "unexpected_error";
    public const string NoHealthData = "no_health_data";
    public const string HealthDataUnreadable = "health_data_unreadable";
}
