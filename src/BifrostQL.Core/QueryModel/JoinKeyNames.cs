namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// Single source of truth for the join-key column aliases that flow
    /// between the SQL emitter and ReaderEnum.
    ///
    /// Single-column joins keep the historical bare names (<c>JoinId</c>,
    /// <c>src_id</c>) so existing data and unit tests stay unchanged.
    /// Composite joins suffix every alias with <c>_&lt;index&gt;</c>.
    ///
    /// Touch this file when changing the convention; do not re-derive the
    /// strings elsewhere.
    /// </summary>
    public static class JoinKeyNames
    {
        public const string JoinIdSingle = "JoinId";
        public const string SrcIdSingle = "src_id";

        public static string JoinIdAt(int index, int count) =>
            count <= 1 ? JoinIdSingle : $"{JoinIdSingle}_{index}";

        public static string SrcIdAt(int index, int count) =>
            count <= 1 ? SrcIdSingle : $"{SrcIdSingle}_{index}";
    }

    /// <summary>
    /// Column aliases carried by per-parent paged nested collections. The
    /// window-paging SQL projects these alongside the child columns; the
    /// result reader strips them from the surfaced rows and reads
    /// <see cref="Total"/> for the paged wrapper. Single source of truth so the
    /// SQL emitter and ReaderEnum stay in sync.
    /// </summary>
    public static class PagedKeys
    {
        public const string RowNumber = "__rn";
        public const string Total = "__total";
    }
}
