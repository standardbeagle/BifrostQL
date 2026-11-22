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
}
