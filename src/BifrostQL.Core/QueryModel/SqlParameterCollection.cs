using System.Collections.Concurrent;

namespace BifrostQL.Core.QueryModel;

public sealed record SqlParameterInfo(string Name, object? Value, string? DbType = null);

public sealed class SqlParameterCollection
{
    private int _counter = 0;
    private readonly ConcurrentDictionary<int, SqlParameterInfo> _parameters = new();

    public IReadOnlyList<SqlParameterInfo> Parameters =>
        _parameters.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();

    public string AddParameter(object? value, string? dbType = null)
    {
        var index = Interlocked.Increment(ref _counter) - 1;
        var name = $"@p{index}";
        _parameters[index] = new SqlParameterInfo(name, value, dbType);
        return name;
    }

    public string AddParameters(IEnumerable<object?> values, string? dbType = null)
    {
        return string.Join(", ", values.Select(v => AddParameter(v, dbType)));
    }
}
