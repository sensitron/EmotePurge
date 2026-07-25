using System.Globalization;
using System.Security.Claims;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class ChannelManagementAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (string.IsNullOrEmpty(channelName))
        {
            return Results.BadRequest(new { error = "Invalid Twitch channel name." });
        }

        var user = context.HttpContext.User;
        var twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var twitchLogin = user.FindFirstValue(TwitchClaimTypes.Login);
        if (twitchUserId is null || twitchLogin is null)
        {
            return Results.Unauthorized();
        }

        // Access token expires ~4h after login (no refresh flow in this pass — see plan/decision
        // log); once it's expired we simply stop attempting the live Helix moderator check.
        var accessToken = user.FindFirstValue(TwitchClaimTypes.AccessToken);
        var expiresAtRaw = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc);
        var tokenStillValid = expiresAtRaw is not null
            && DateTime.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt)
            && expiresAt > DateTime.UtcNow;

        var principal = new TwitchPrincipalInfo(twitchUserId, twitchLogin, tokenStillValid ? accessToken : null);

        var accessService = context.HttpContext.RequestServices.GetRequiredService<IChannelAccessService>();
        var allowed = await accessService.CanManageChannelAsync(principal, channelName, context.HttpContext.RequestAborted);

        return allowed ? await next(context) : Results.Forbid();
    }
}
