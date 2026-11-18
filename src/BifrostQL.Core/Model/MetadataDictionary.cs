using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BifrostQL.Core.Model;

/// <summary>
/// The metadata store behind <see cref="DbTable.Metadata"/>, <see cref="ColumnDto.Metadata"/>
/// and <see cref="DbModel.Metadata"/>. It owns its backing dictionary and bumps one
/// process-wide <see cref="Version"/> on every write, which is the change signal
/// <see cref="MetadataMemo{TValue}"/> needs to serve a parsed config without re-parsing it on
/// every call. Production writes metadata only while the model is built; hosts and tests that
/// write after load bump the version and drop every memo.
/// A plain dictionary handed to an <c>init</c> setter is copied (<see cref="ShareOrCopy"/>), so
/// a later write through the caller's original reference changes only that copy and never the
/// table's metadata; only an existing <see cref="MetadataDictionary"/> is shared.
/// </summary>
public sealed class MetadataDictionary : IDictionary<string, object?>
{
    private static long _version;
    private readonly IDictionary<string, object?> _inner;

    public MetadataDictionary() : this(new Dictionary<string, object?>()) { }

    private MetadataDictionary(Dictionary<string, object?> inner) => _inner = inner;

    /// <summary>The number of metadata writes so far, process-wide.</summary>
    public static long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// <paramref name="metadata"/> itself when it already counts writes (copies that share one
    /// store, such as <see cref="DbTable.WithGraphQlName"/>, stay shared), else
    /// <see cref="CopyOf"/> it. A plain dictionary is never wrapped by reference: a later write
    /// through the caller's reference would change the store without bumping the version and
    /// a memo would keep serving the config parsed before it.
    /// </summary>
    public static MetadataDictionary ShareOrCopy(IDictionary<string, object?> metadata)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        return metadata as MetadataDictionary ?? CopyOf(metadata);
    }

    /// <summary>A new store holding <paramref name="metadata"/>'s entries, with its key comparer when it has one.</summary>
    public static MetadataDictionary CopyOf(IDictionary<string, object?> metadata)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        var source = metadata is MetadataDictionary owned ? owned._inner : metadata;
        return new MetadataDictionary(source is Dictionary<string, object?> plain
            ? new Dictionary<string, object?>(plain, plain.Comparer)
            : new Dictionary<string, object?>(source));
    }

    private static void Changed() => Interlocked.Increment(ref _version);

    public object? this[string key]
    {
        get => _inner[key];
        set { _inner[key] = value; Changed(); }
    }

    public ICollection<string> Keys => _inner.Keys;
    public ICollection<object?> Values => _inner.Values;
    public int Count => _inner.Count;
    public bool IsReadOnly => _inner.IsReadOnly;

    public void Add(string key, object? value) { _inner.Add(key, value); Changed(); }
    public void Add(KeyValuePair<string, object?> item) { _inner.Add(item); Changed(); }

    public void Clear() { _inner.Clear(); Changed(); }

    public bool Remove(string key)
    {
        var removed = _inner.Remove(key);
        if (removed) Changed();
        return removed;
    }

    public bool Remove(KeyValuePair<string, object?> item)
    {
        var removed = _inner.Remove(item);
        if (removed) Changed();
        return removed;
    }

    public bool Contains(KeyValuePair<string, object?> item) => _inner.Contains(item);
    public bool ContainsKey(string key) => _inner.ContainsKey(key);
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value) => _inner.TryGetValue(key, out value);
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A per-table memo of a config parsed from metadata (policy, computed columns). An entry is
/// served only while <see cref="MetadataDictionary.Version"/> is unchanged since it was
/// computed and the same context objects (model, EAV configs) are passed; any metadata write
/// anywhere recomputes. Only a concrete <see cref="DbTable"/> is memoized: other
/// <see cref="IDbTable"/> implementations carry no write signal. A throw is never cached.
/// </summary>
internal sealed class MetadataMemo<TValue>
{
    private sealed record Entry(long Version, object? Context1, object? Context2, TValue Value);

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbTable, Entry> _entries = new();

    public TValue GetOrCompute(IDbTable table, object? context1, object? context2, Func<TValue> compute)
    {
        if (table is not DbTable dbTable)
            return compute();

        // Read the version BEFORE computing: a write that lands during the compute leaves
        // this entry stale, so the next call recomputes rather than serving it.
        var version = MetadataDictionary.Version;
        if (_entries.TryGetValue(dbTable, out var entry)
            && entry.Version == version
            && ReferenceEquals(entry.Context1, context1)
            && ReferenceEquals(entry.Context2, context2))
            return entry.Value;

        var value = compute();
        _entries.AddOrUpdate(dbTable, new Entry(version, context1, context2, value));
        return value;
    }
}
