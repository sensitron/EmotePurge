using System.Globalization;
using System.Text.RegularExpressions;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/api/channels/{channelName}/join", async (
    string channelName,
    IChannelService channelService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var channel = await channelService.JoinAsync(channelName, ct);
    return Results.Ok(new { channelId = channel.Id, channelName = channel.ChannelName, channel.IsBotActive });
});

app.MapDelete("/api/channels/{channelName}", async (
    string channelName,
    IChannelService channelService,
    CancellationToken ct) =>
{
    var removed = await channelService.LeaveAsync(channelName, ct);
    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/channels/{channelName}/usage-stats", async (
    string channelName,
    IUsageStatQueryService usageStatQueryService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var stats = await usageStatQueryService.GetUsageStatsAsync(channelName, ct);
    return Results.Ok(stats);
});

app.MapGet("/api/channels/{channelName}/usage-stats/totals", async (
    string channelName,
    string from,
    string to,
    IUsageStatQueryService usageStatQueryService,
    CancellationToken ct) =>
{
    if (!Regex.IsMatch(channelName.Trim().ToLowerInvariant(), "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
        !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
    {
        return Results.BadRequest(new { error = "'from'/'to' must be ISO dates (yyyy-MM-dd)." });
    }

    if (fromDate > toDate)
    {
        return Results.BadRequest(new { error = "'from' must be on or before 'to'." });
    }

    const int maxRangeDays = 366;
    if (toDate.DayNumber - fromDate.DayNumber > maxRangeDays)
    {
        return Results.BadRequest(new { error = $"Range too large; max {maxRangeDays} days." });
    }

    var totals = await usageStatQueryService.GetUsageTotalsAsync(channelName, fromDate, toDate, ct);
    return Results.Ok(totals);
});

app.Run();
