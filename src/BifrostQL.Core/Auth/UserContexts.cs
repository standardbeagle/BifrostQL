namespace BifrostQL.Core.Auth;

/// <summary>
/// The one way to copy a user context. The HTTP user context compares keys case-insensitively,
/// and a plain <c>new Dictionary&lt;string, object?&gt;(userContext)</c> silently drops that
/// comparer: a token carrying <c>Org_Id</c> under <c>tenant-context-key: org_id</c> then scoped
/// the surfaces that read the request context and refused the ones that read a copy.
/// <c>UserContextCopySourceScanTests</c> pins that no surface copies a user context any other way.
/// </summary>
public static class UserContexts
{
    /// <summary>
    /// A mutable copy of <paramref name="source"/> whose keys compare with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. Fails fast when the source holds two keys
    /// that differ only in case (an ordinal context built by a host): which of the two the security
    /// modules would read is undefined.
    /// </summary>
    public static Dictionary<string, object?> Copy(IEnumerable<KeyValuePair<string, object?>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (!copy.TryAdd(key, value))
                throw new InvalidOperationException(
                    $"The user context holds keys that differ only in case ('{key}'); one key per name is required.");
        }
        return copy;
    }

    /// <summary>
    /// How deep <see cref="Snapshot"/> follows nested collections. A context that nests deeper
    /// (or refers to itself) is refused rather than overflowing the stack.
    /// </summary>
    public const int MaxSnapshotDepth = 16;

    /// <summary>
    /// <see cref="Copy"/> that also copies every collection value, recursively, so the result
    /// shares no mutable collection with <paramref name="source"/>: a sequence of strings (a host's
    /// <c>List&lt;string&gt;</c>, the <c>roles</c>/<c>permissions</c> arrays) becomes a fresh
    /// <c>string[]</c>; an array of value types a clone of that array; an
    /// <c>IDictionary&lt;string, object?&gt;</c> or <c>IReadOnlyDictionary&lt;string, object?&gt;</c>
    /// a fresh <c>Dictionary&lt;string, object?&gt;</c> (same comparer when the source is a
    /// <c>Dictionary</c>); and any other sequence (a <c>List&lt;int&gt;</c>, a
    /// <c>HashSet&lt;Guid&gt;</c>, an <c>object[]</c>) a fresh <c>object?[]</c> of its boxed items,
    /// which the tenant reader and the column coercer read like any other array. A dictionary of any
    /// other shape (<c>Dictionary&lt;string, string&gt;</c>) throws: it cannot be copied without
    /// changing its type, and sharing it would leak one caller's writes to another. The identity
    /// projection memo stores a snapshot and hands each caller its own snapshot, so one caller
    /// writing into a collection never changes the context another caller (or a later gate) reads.
    /// Scalars (strings, numbers, a <c>Guid</c>, a <c>ClaimsPrincipal</c>) are kept by reference.
    /// </summary>
    public static Dictionary<string, object?> Snapshot(IEnumerable<KeyValuePair<string, object?>> source)
    {
        var copy = Copy(source);
        foreach (var key in copy.Keys.ToArray())
            copy[key] = SnapshotValue(copy[key], key, depth: 1);
        return copy;
    }

    private static object? SnapshotValue(object? value, string key, int depth)
    {
        if (value is null or string)
            return value;
        if (value is not System.Collections.IEnumerable sequence)
            return value;
        if (depth > MaxSnapshotDepth)
            throw new InvalidOperationException(
                $"The user context value '{key}' nests collections deeper than {MaxSnapshotDepth} levels.");

        return value switch
        {
            IEnumerable<string> strings => strings.ToArray(),
            IReadOnlyDictionary<string, object?> map => SnapshotMap(map, (map as Dictionary<string, object?>)?.Comparer, key, depth),
            IDictionary<string, object?> map => SnapshotMap(map, (map as Dictionary<string, object?>)?.Comparer, key, depth),
            System.Collections.IDictionary => throw new InvalidOperationException(
                $"The user context value '{key}' is a {value.GetType().Name}; a dictionary value must be an " +
                "IDictionary<string, object?> so it can be snapshotted."),
            Array array when array.GetType().GetElementType()!.IsValueType => array.Clone(),
            _ => sequence.Cast<object?>().Select(item => SnapshotValue(item, key, depth + 1)).ToArray(),
        };
    }

    private static Dictionary<string, object?> SnapshotMap(
        IEnumerable<KeyValuePair<string, object?>> map, IEqualityComparer<string>? comparer, string key, int depth)
    {
        var copy = new Dictionary<string, object?>(comparer);
        foreach (var (name, item) in map)
            copy.Add(name, SnapshotValue(item, key, depth + 1));
        return copy;
    }
}
