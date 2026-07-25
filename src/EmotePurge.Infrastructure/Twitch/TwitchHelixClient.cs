using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Twitch;

public class TwitchHelixClient(HttpClient httpClient, ILogger<TwitchHelixClient> logger) : ITwitchHelixClient
{
    private const int MaxModeratedChannelPages = 10;

    public async Task<TwitchUserInfo?> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "users");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch Get Users fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<TwitchGetUsersResponseDto>(TwitchJsonOptions.Value, cancellationToken);
            var user = dto?.Data.FirstOrDefault();
            return user is null ? null : new TwitchUserInfo(user.Id, user.Login, user.DisplayName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch Get Users fehlgeschlagen.");
            return null;
        }
    }

    public async Task<IReadOnlySet<string>?> GetModeratedChannelLoginsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken = default)
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;

        try
        {
            for (var page = 0; page < MaxModeratedChannelPages; page++)
            {
                var url = $"moderation/channels?user_id={twitchUserId}&first=100";
                if (cursor is not null)
                {
                    url += $"&after={cursor}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Twitch Get Moderated Channels fehlgeschlagen mit Status {Status}.", response.StatusCode);
                    return null;
                }

                var dto = await response.Content.ReadFromJsonAsync<TwitchGetModeratedChannelsResponseDto>(TwitchJsonOptions.Value, cancellationToken);
                if (dto is null)
                {
                    return null;
                }

                foreach (var channel in dto.Data)
                {
                    logins.Add(channel.BroadcasterLogin);
                }

                cursor = dto.Pagination?.Cursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }

            return logins;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch Get Moderated Channels fehlgeschlagen für User {UserId}.", twitchUserId);
            return null;
        }
    }

    public async Task<bool?> GetUserSubscriptionStatusAsync(string accessToken, string broadcasterTwitchId, string userTwitchId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"subscriptions/user?broadcaster_id={broadcasterTwitchId}&user_id={userTwitchId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch Get User Subscription fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<TwitchGetUserSubscriptionResponseDto>(TwitchJsonOptions.Value, cancellationToken);
            return dto?.Data.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch Get User Subscription fehlgeschlagen für User {UserId}/Broadcaster {BroadcasterId}.", userTwitchId, broadcasterTwitchId);
            return null;
        }
    }
}
