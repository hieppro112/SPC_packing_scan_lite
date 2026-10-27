namespace ScaleScanLite.Core.Logic;

public readonly record struct PageInfo(int Page, int PageSize, int TotalItems)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);
    public int FromIndex => TotalItems == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int ToIndex => Math.Min(Page * PageSize, TotalItems);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PageInfo Create(int page, int pageSize, int totalItems)
    {
        pageSize = Math.Max(1, pageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalItems) / (double)pageSize));
        return new PageInfo(Math.Clamp(page, 1, totalPages), pageSize, Math.Max(0, totalItems));
    }
}
