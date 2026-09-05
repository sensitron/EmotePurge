namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Bound from configuration section <c>ChatLogArchive:*</c>. A plain POCO rather than an
/// <c>IOptions</c>-wrapped type — the HttpClient registration needs <see cref="BaseUrl"/> already
/// bound at registration time to set <c>HttpClient.BaseAddress</c>, not behind a snapshot
/// indirection.
/// </summary>
public sealed class ChatLogArchiveOptions
{
    /// <summary>
    /// Measured live 2026-09-05 (T8): a justlog-compatible aggregator over several upstream
    /// Justlog instances.
    /// </summary>
    public string BaseUrl { get; set; } = "https://logs.zonian.dev/";

    /// <summary>
    /// Minimum time between the start of consecutive requests. A self-imposed courtesy pace, not a
    /// measured limit — the archive documents no contract and sends no rate-limit headers
    /// (Premise 5 of the design).
    /// </summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Deadline for the response body to finish once headers have arrived. Separate from
    /// <c>HttpClient.Timeout</c>, which <c>HttpCompletionOption.ResponseHeadersRead</c> makes cover
    /// only the header phase.
    /// </summary>
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
