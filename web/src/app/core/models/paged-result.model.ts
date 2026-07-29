// Matches EmotePurge.Core.Services.PagedResult<T> — general paging envelope, not voting-specific.
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}
