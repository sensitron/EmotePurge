using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class ChannelManagementAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Full format check (not just non-empty) before any Redis/external-system access — an
        // unvalidated route value would otherwise reach ModRoleCache.BuildKey as a Redis key part.
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        if (channelName is null || !ChannelNameValidation.IsValid(channelName))
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
