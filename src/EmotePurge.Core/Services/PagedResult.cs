namespace EmotePurge.Core.Services;

// General-purpose paging envelope, not vote-session-specific — lives here so other list endpoints
// can adopt the same shape later instead of inventing their own.
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
