using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

public class VoteEligibilityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var channelName = context.HttpContext.Request.RouteValues["channelName"] as string;
        var sessionIdRaw = context.HttpContext.Request.RouteValues["sessionId"] as string;
        if (string.IsNullOrEmpty(channelName) || !long.TryParse(sessionIdRaw, out var sessionId))
        {
            return Results.BadRequest(new { error = "Invalid channel name or session id." });
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
            VoteEligibilityResult.SessionNotFound => Results.NotFound(new { error = "Vote session not found." }),
            VoteEligibilityResult.SessionEnded => Results.Conflict(new { error = "Vote session has ended." }),
            VoteEligibilityResult.RoleNotEligible => Results.Forbid(),
            _ => Results.Forbid()
        };
    }
}
