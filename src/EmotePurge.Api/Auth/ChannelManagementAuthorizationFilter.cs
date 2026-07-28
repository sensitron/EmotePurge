using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class ChannelManagementAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (string.IsNullOrEmpty(channelName))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        var principal = context.HttpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var accessService = context.HttpContext.RequestServices.GetRequiredService<IChannelAccessService>();
        var allowed = await accessService.CanManageChannelAsync(principal, channelName, context.HttpContext.RequestAborted);

        return allowed ? await next(context) : Results.Forbid();
    }
}
