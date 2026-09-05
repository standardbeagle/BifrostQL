using System.Collections.Concurrent;

namespace BifrostQL.Core.QueryModel;

public sealed record SqlParameterInfo(string Name, object? Value, string? DbType = null);

public sealed class SqlParameterCollection
{
    private int _counter = 0;
    private readonly ConcurrentDictionary<int, SqlParameterInfo> _parameters = new();

    public IReadOnlyList<SqlParameterInfo> Parameters =>
        _parameters.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();

    /// <summary>
    /// Binds <paramref name="value"/> under a fresh generated name and returns the
    /// bound <see cref="SqlParameterInfo"/>. Returning the info (not just the name)
    /// lets a caller keep exactly the parameter it added — re-reading
    /// <see cref="Parameters"/> per bind re-materializes and re-sorts the whole
    /// collection, which made TableFilter's per-value binds O(n² log n) on wide
    /// <c>_in</c> lists.
    /// </summary>
    public SqlParameterInfo AddParameter(object? value, string? dbType = null)
    {
        var index = Interlocked.Increment(ref _counter) - 1;
        // SqlParameterNames owns this shape and reserves it against column-derived
        // names, so a table with a column literally named "p0" cannot produce the
        // same parameter and hijack a transformer-injected predicate.
        var info = new SqlParameterInfo($"@{SqlParameterNames.Generated(index)}", value, dbType);
        _parameters[index] = info;
        return info;
    }

    public string AddParameters(IEnumerable<object?> values, string? dbType = null)
    {
        return string.Join(", ", values.Select(v => AddParameter(v, dbType).Name));
    }
}
