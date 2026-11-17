using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.ComputedColumns;

public sealed class ComputedColumnContext
{
    public required IDbModel Model { get; init; }
    public required IDbTable Table { get; init; }
    public required ComputedColumnDefinition Column { get; init; }
    public required IReadOnlyDictionary<string, object?> Row { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public IServiceProvider? Services { get; init; }

    /// <summary>
    /// The connection factory for the database backing the current request, when
    /// available. Providers that must run their own auxiliary query (e.g. the EAV
    /// <c>_meta</c> provider reading a row's attribute rows) open a connection and
    /// build dialect-escaped, parameterized SQL through this. Null when the query
    /// was resolved without a connection factory in context.
    /// </summary>
    public IDbConnFactory? ConnFactory { get; init; }
}

public interface IComputedColumnProvider
{
    string Name { get; }

    ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// A provider that computes a whole result set's values in one call, for a value that
/// needs its own read: one query for the page instead of one per row. The execution
/// manager calls <see cref="ComputeBatchAsync"/> instead of the per-row
/// <see cref="IComputedColumnProvider.ComputeAsync"/> when a provider implements it.
/// </summary>
public interface IBatchComputedColumnProvider : IComputedColumnProvider
{
    /// <summary>
    /// One value per row of <see cref="ComputedColumnBatchContext.Rows"/>, in the same order.
    /// </summary>
    ValueTask<IReadOnlyList<object?>> ComputeBatchAsync(ComputedColumnBatchContext context, CancellationToken cancellationToken = default);
}

public sealed class ComputedColumnBatchContext
{
    public required IDbModel Model { get; init; }
    public required IDbTable Table { get; init; }
    public required ComputedColumnDefinition Column { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public IServiceProvider? Services { get; init; }
    public IDbConnFactory? ConnFactory { get; init; }
}

public interface IComputedColumnProviders : IReadOnlyCollection<IComputedColumnProvider>
{
    bool TryGet(string name, out IComputedColumnProvider provider);
}

public sealed class ComputedColumnProviders : IComputedColumnProviders
{
    public static ComputedColumnProviders Empty { get; } = new(Array.Empty<IComputedColumnProvider>());

    private readonly IReadOnlyList<IComputedColumnProvider> _providers;
    private readonly Dictionary<string, IComputedColumnProvider> _lookup;

    public ComputedColumnProviders(IEnumerable<IComputedColumnProvider> providers)
    {
        _providers = providers.ToArray();
        _lookup = _providers
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _providers.Count;

    public bool TryGet(string name, out IComputedColumnProvider provider)
        => _lookup.TryGetValue(name, out provider!);

    public IEnumerator<IComputedColumnProvider> GetEnumerator() => _providers.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
