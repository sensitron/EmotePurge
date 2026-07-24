using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Infrastructure;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
    AppDbContext db,
    IRedisPublisher redisPublisher,
    CancellationToken ct) =>
{
    var normalized = channelName.Trim().ToLowerInvariant();
    if (!Regex.IsMatch(normalized, "^[a-z0-9_]{4,25}$"))
    {
        return Results.BadRequest(new { error = "Invalid Twitch channel name." });
    }

    var channel = await db.Channels.SingleOrDefaultAsync(c => c.ChannelName == normalized, ct);
    if (channel is null)
    {
        channel = new Channel { ChannelName = normalized, IsBotActive = true };
        db.Channels.Add(channel);
    }
    else
    {
        channel.IsBotActive = true;
    }

    await db.SaveChangesAsync(ct);
    await redisPublisher.PublishAsync("channel:bot:commands", $"JOIN:{normalized}", ct);

    return Results.Ok(new { channelId = channel.Id, channelName = normalized, channel.IsBotActive });
});

app.Run();
