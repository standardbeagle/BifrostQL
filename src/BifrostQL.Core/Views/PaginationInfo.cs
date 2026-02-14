namespace BifrostQL.Core.Views
{
    /// <summary>
    /// Describes the current pagination state for a list view,
    /// including page position and total counts.
    /// </summary>
    public sealed class PaginationInfo
    {
        public PaginationInfo(int currentPage, int pageSize, int totalRecords)
        {
            CurrentPage = Math.Max(1, currentPage);
            PageSize = Math.Max(1, pageSize);
            TotalRecords = Math.Max(0, totalRecords);
            TotalPages = TotalRecords == 0 ? 1 : (int)Math.Ceiling((double)TotalRecords / PageSize);
        }

        public int CurrentPage { get; }
        public int PageSize { get; }
        public int TotalRecords { get; }
        public int TotalPages { get; }

        public bool HasPrevious => CurrentPage > 1;
        public bool HasNext => CurrentPage < TotalPages;
    }
}
