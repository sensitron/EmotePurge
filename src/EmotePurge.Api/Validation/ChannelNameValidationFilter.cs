namespace EmotePurge.Api.Validation;

/// <summary>
/// Validates the <c>channelName</c> route value once per group, instead of the ten hand-written
/// <c>if (!ChannelNameValidation.IsValid(...)) return BadRequest(...)</c> blocks that used to sit in
/// the handlers — five channel-scoped endpoints had no check at all, and nothing in the code said
/// whether that was an omission or deliberate.
/// <para>
/// Register it as the <em>first</em> filter on a group, ahead of any authorization filter: the
/// contract is that a malformed name is a 400 with <c>invalid_channel_name</c>, not a 403 and not a
/// 404. A route constraint (<c>{channelName:regex(...)}</c>) would be shorter but answers 404 without
/// a body — and, being unable to normalize, would reject the mixed-case logins Twitch users actually
/// type. See conflict Z4 in the review.
/// </para>
/// <para>
/// Groups may legitimately contain endpoints without that route value (e.g. <c>GET /api/channels</c>
/// and <c>GET /api/channels/mine</c> under the <c>/api/channels</c> group). Those pass through
/// untouched — this filter validates a value when there is one, it does not require one.
/// </para>
/// </summary>
public class ChannelNameValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Request.RouteValues.TryGetValue("channelName", out var routeValue) &&
            (routeValue is not string channelName || !ChannelNameValidation.IsValid(channelName)))
        {
            return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidChannelName });
        }

        return await next(context);
    }
}
