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
    /// <see cref="Copy"/> that also copies every collection value, so the result shares no
    /// mutable state with <paramref name="source"/>: a sequence of strings (a host's
    /// <c>List&lt;string&gt;</c>, the <c>roles</c>/<c>permissions</c> arrays) becomes a fresh
    /// <c>string[]</c>, any other array a clone, and any other non-string sequence of references
    /// a fresh <c>object?[]</c>. The identity projection memo stores a snapshot and hands each
    /// caller its own snapshot, so one caller writing into a collection never changes the context
    /// another caller (or a later gate) reads. Dictionaries and other scalar values are copied by
    /// reference.
    /// </summary>
    public static Dictionary<string, object?> Snapshot(IEnumerable<KeyValuePair<string, object?>> source)
    {
        var copy = Copy(source);
        foreach (var key in copy.Keys.ToArray())
            copy[key] = SnapshotValue(copy[key]);
        return copy;
    }

    private static object? SnapshotValue(object? value) => value switch
    {
        null or string => value,
        IEnumerable<string> strings => strings.ToArray(),
        Array array => array.Clone(),
        IEnumerable<object?> items => items.ToArray(),
        _ => value,
    };
}
