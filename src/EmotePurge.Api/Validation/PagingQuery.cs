namespace EmotePurge.Api.Validation;

/// <summary>
/// The paging contract every list endpoint shares. Out-of-range paging is corrected silently rather
/// than rejected, so it introduces no <see cref="ApiErrorCodes"/> entry — and none has to be
/// mirrored into both locale files for something a client can only hit by constructing the URL by
/// hand.
/// <para>
/// Extracted after the fourth verbatim copy of the same two lines. The query services document
/// their arguments as "expected to be pre-validated by the endpoint"; this is that validation, in
/// one place, so the cap cannot drift between endpoints.
/// </para>
/// </summary>
internal static class PagingQuery
{
    private const int DefaultPageSize = 20;

    /// <summary>
    /// Clamps a 1-based page and its size. <paramref name="maxPageSize"/> is the ceiling a caller
    /// may ask for — it exists so one client cannot turn a paged endpoint into a full-table read.
    /// </summary>
    public static (int Page, int PageSize) Clamp(int page, int pageSize, int maxPageSize = 100)
        => (page <= 0 ? 1 : page,
            pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, maxPageSize));

    /// <summary>
    /// Trims a free-text filter and treats blank as absent, so `?actor=` and a missing `actor` mean
    /// the same thing to the query services.
    /// </summary>
    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
