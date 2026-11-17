namespace BifrostQL.Core.Resolvers;

/// <summary>
/// A parent row's key as a grouping key for child rows read back by foreign key. The
/// parent's key and the child's FK come back from the provider as possibly different CLR
/// types (int vs long, decimal), so numbers compare by value and byte arrays by content.
/// The one home for "place each child row under its parent" on the batched read paths
/// (tree-sync child loads, the EAV <c>_meta</c> page read).
/// </summary>
internal readonly record struct ParentKey(object Value)
{
    /// <summary>
    /// The largest number of parent keys bound into one <c>IN</c> list. SQL Server caps a
    /// command at 2100 parameters, and a row filter adds its own.
    /// </summary>
    public const int MaxKeysPerQuery = 500;

    public static ParentKey Of(object value) => new(value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal
            => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value,
    });

    /// <summary>
    /// The rows grouped under <paramref name="pk"/>. A text parent key matched against a uuid
    /// FK (Postgres casts the bound text to uuid) comes back as a Guid, not the parent's
    /// string, so an exact miss on a string that parses as a uuid retries under the Guid.
    /// Text children group under string keys, so the retry never places one under another
    /// parent.
    /// </summary>
    public static bool TryGetGroup<T>(IReadOnlyDictionary<ParentKey, T> groups, object pk, out T group)
    {
        if (groups.TryGetValue(Of(pk), out group!))
            return true;
        if (pk is string text && Guid.TryParse(text, out var uuid) && groups.TryGetValue(Of(uuid), out group!))
            return true;
        group = default!;
        return false;
    }
}
