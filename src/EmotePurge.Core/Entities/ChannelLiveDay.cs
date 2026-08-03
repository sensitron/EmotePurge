namespace EmotePurge.Core.Entities;

// Live coverage of a channel per UTC calendar day (idea A10), accumulated by the worker's periodic
// Helix streams poll: every poll that sees the channel live credits one poll interval's worth of
// minutes. Approximate by design — stream starts/ends between two polls round to the interval.
// The day resolution deliberately matches UsageStat.Date, so "was there a stream that day" lines
// up 1:1 with the daily usage series; the minutes (rather than a bare flag) are what stage 2
// ("usage per live hour") will build on. Rows only exist since the poll shipped: an absent row
// means "no data", never "offline".
public class ChannelLiveDay
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;

    // UTC calendar day, same resolution as UsageStat.Date.
    public DateOnly Date { get; set; }
    public int LiveMinutes { get; set; }

    public Channel Channel { get; set; } = null!;
}
