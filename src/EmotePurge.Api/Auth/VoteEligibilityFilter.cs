using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class VoteEligibilityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Full format check (not just non-empty) before any Redis/external-system access — an
        // unvalidated route value would otherwise reach ModRoleCache.BuildKey as a Redis key part.
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        var sessionIdRaw = context.HttpContext.Request.RouteValues["sessionId"] as string;
        if (channelName is null || !ChannelNameValidation.IsValid(channelName) || !long.TryParse(sessionIdRaw, out var sessionId))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelOrSessionId });
        }

        var principal = context.HttpContext.User.TryBuildTwitchPrincipal();
        if (principal is null)
        {
            return Results.Unauthorized();
        }

        var eligibilityService = context.HttpContext.RequestServices.GetRequiredService<IVoteEligibilityService>();
        var result = await eligibilityService.EvaluateAsync(principal, channelName, sessionId, context.HttpContext.RequestAborted);

        return result switch
        {
            VoteEligibilityResult.Allowed => await next(context),
            VoteEligibilityResult.SessionNotFound => Results.NotFound(new { errorCode = ApiErrorCodes.VoteSessionNotFound }),
            VoteEligibilityResult.SessionEnded => Results.Conflict(new { errorCode = ApiErrorCodes.VoteSessionEnded }),
            VoteEligibilityResult.RoleNotEligible => Results.Forbid(),
            _ => Results.Forbid()
        };
    }
}
