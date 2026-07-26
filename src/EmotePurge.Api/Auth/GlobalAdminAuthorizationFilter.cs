using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class GlobalAdminAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var principal = context.HttpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var accessService = context.HttpContext.RequestServices.GetRequiredService<IChannelAccessService>();
        return accessService.IsGlobalAdmin(principal) ? await next(context) : Results.Forbid();
    }
}
