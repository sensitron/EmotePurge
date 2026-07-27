using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

// Gates VIEW access to a vote session's results — distinct from VoteEligibilityFilter (which gates
// casting a vote): uses EvaluateAudienceAsync, which ignores whether the session has already ended,
// so a session's intended audience can still see final results after voting closes. Anonymous
// viewing was intentionally removed (see CLAUDE.md decision log) — every viewer must now be logged
// in and part of the session's target audience.
public class VoteAudienceFilter : IEndpointFilter
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
        var result = await eligibilityService.EvaluateAudienceAsync(principal, channelName, sessionId, context.HttpContext.RequestAborted);

        return result switch
        {
            VoteEligibilityResult.Allowed => await next(context),
            VoteEligibilityResult.SessionNotFound => Results.NotFound(new { error = "Vote session not found." }),
            VoteEligibilityResult.RoleNotEligible => Results.Forbid(),
            _ => Results.Forbid()
        };
    }
}
