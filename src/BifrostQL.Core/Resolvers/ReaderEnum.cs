using System.Collections;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Resolvers
{
    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> _tables;
        private readonly GqlObjectQuery _tableSql;
        private readonly EnumColumnMap? _enumColumns;
        private readonly ILogger? _logger;

        // Per-result-set indexes, built once and shared across every parent probe
        // (including nested sub-readers, which route back through this root). Keyed by
        // join name / aggregate SQL key, both unique within _tables. Concurrent so
        // sibling/list field resolution stays safe; GetOrAdd may build twice under a
        // race, which is idempotent.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JoinRowIndex> _joinIndexes = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JoinRowIndex> _aggregateIndexes = new(StringComparer.Ordinal);

        private JoinRowIndex GetJoinIndex(string joinName, (IDictionary<string, int> index, IList<object?[]> data) tableData, int keyLength)
            => _joinIndexes.GetOrAdd(joinName, _ =>
            {
                var indices = new int[keyLength];
                for (var i = 0; i < keyLength; i++)
                    indices[i] = tableData.index[JoinKeyNames.SrcIdAt(i, keyLength)];
                return JoinRowIndex.Build(tableData.data, indices);
            });

        private JoinRowIndex GetAggregateIndex(string sqlKey, (IDictionary<string, int> index, IList<object?[]> data) tableData, int keyIndex)
            => _aggregateIndexes.GetOrAdd(sqlKey, _ => JoinRowIndex.Build(tableData.data, new[] { keyIndex }));

        public ReaderEnum(
            GqlObjectQuery gqlObjectQuery,
            IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> tableData,
            EnumColumnMap? enumColumns = null,
            ILogger? logger = null)
        {
            _tableSql = gqlObjectQuery;
            _tables = tableData;
            _enumColumns = enumColumns;
            _logger = logger;
        }

        public ValueTask<object?> Get(int row, IBifrostFieldContext context)
        {
            var table = _tables[_tableSql.KeyName];
            var name = context.FieldName;
            var alias = context.FieldAlias;
            var found = table.index.TryGetValue(alias ?? name, out var index);
            if (found)
            {
                var raw = DbConvert(table.data[row][index]);
                return ValueTask.FromResult(MapEnumValueOrRaw(_tableSql.DbTable.DbName, name, raw));
            }
            return GetDataForMissingColumn(context, table, row);
        }

        /// <summary>
        /// Maps a stored enum-column value to its GraphQL enum name. Non-enum
        /// columns pass through untouched. A stored value that is not a declared
        /// enum member (drift) resolves to null and emits a structured warning.
        /// Shared by the top-level read and by nested projections
        /// (<see cref="SingleRowLookup"/>, <see cref="SubTableEnumerable"/>),
        /// which pass their own table DbName so the single mapping/drift policy
        /// applies uniformly regardless of read depth.
        /// </summary>
        internal object? MapEnumValueOrRaw(string tableDbName, string field, object? raw)
        {
            if (_enumColumns == null)
                return raw;

            if (!_enumColumns.TryGetEnumType(tableDbName, field, out _))
                return raw;

            var mapped = _enumColumns.ValueToName(tableDbName, field, raw);
            if (mapped != null)
                return mapped;

            if (raw != null)
                _logger?.LogWarning(
                    "Enum drift: value '{Value}' on {Table}.{Field} is not a declared enum member; returning null.",
                    raw, tableDbName, field);
            return null;
        }
        public ValueTask<object?> GetDataForMissingColumn(IBifrostFieldContext context, (IDictionary<string, int> index, IList<object?[]> data) table, int row)
            => GetDataForMissingColumn(context, _tableSql, table, row);

        /// <summary>
        /// Resolves a join/aggregate field against the supplied query node
        /// (<paramref name="level"/>) rather than always the root. Nested
        /// sub-readers pass their own connected-table node so joins resolve
        /// against the correct level's direct children — otherwise a same-named
        /// join on a different path shadows the right one and the deeper field
        /// reads the wrong (or empty) result set, surfacing as null.
        /// </summary>
        internal ValueTask<object?> GetDataForMissingColumn(IBifrostFieldContext context, GqlObjectQuery level, (IDictionary<string, int> index, IList<object?[]> data) table, int row)
        {
            string name = context.FieldName;
            string? alias = context.FieldAlias;

            var join = level.GetJoin(alias, name);
            if (join != null)
                return GetJoinResult(table, row, join);

            var aggregate = level.GetAggregate(alias, name);
            if (aggregate != null)
            {
                var tableData = _tables[aggregate.SqlKey!];
                var valueFound = tableData.index.TryGetValue(aggregate.FinalColumnGraphQlName, out int valueIndex);
                var keyFound = tableData.index.TryGetValue("srcId", out int keyIndex);
                if (!valueFound || !keyFound)
                    throw new BifrostExecutionError($"Unable to find aggregate column: {name} on table: {level.Alias}:{level.GraphQlName}");
                var parentKeyIndex = table.index[level.DbTable.KeyColumns.First().DbName];
                var parentKeyValue = table.data[row][parentKeyIndex];
                var aggregateIndex = GetAggregateIndex(aggregate.SqlKey!, tableData, keyIndex);
                var value = aggregateIndex.First(new[] { parentKeyValue })?[valueIndex];
                return ValueTask.FromResult<object?>(value);
            }
            throw new BifrostExecutionError($"Unable to find queryField: {name} on table: {level.Alias}:{level.GraphQlName}");
        }

        private ValueTask<object?> GetJoinResult((IDictionary<string, int> index, IList<object?[]> data) table, int row, TableJoin join)
        {
            var key = JoinKeyValues.FromParentRow(join, table, row);
            var tableData = _tables[join.JoinName];
            var connected = join.ConnectedTable;

            if (join.QueryType == QueryType.Join)
            {
                // A null key (nullable FK) references nothing, so the child
                // collection is empty rather than a failed query.
                var matched = key == null
                    ? (IList<object?[]>)Array.Empty<object?[]>()
                    : GetJoinIndex(join.JoinName, tableData, key.Length).Match(key);
                var subTable = new SubTableEnumerable(this, matched, tableData, connected);
                // Paged nested collections (multi-links) surface the same
                // wrapper as top-level queries: {total, offset, limit, data}.
                // The per-parent total is carried on the window column; read it
                // from the first matched row, or 0 when the parent has none.
                if (join.ConnectedTable.IncludeResult)
                {
                    var total = key == null
                        ? 0
                        : ReadPerParentTotal(tableData, GetJoinIndex(join.JoinName, tableData, key.Length), key);
                    return ValueTask.FromResult<object?>(new TableResult
                    {
                        Total = total,
                        Offset = join.ConnectedTable.Offset,
                        Limit = join.ConnectedTable.Limit,
                        Data = subTable,
                    });
                }
                return ValueTask.FromResult<object?>(subTable);
            }
            if (join.QueryType == QueryType.Single)
            {
                // A null key (nullable FK) resolves the single-link to null.
                var data = key == null
                    ? null
                    : GetJoinIndex(join.JoinName, tableData, key.Length).First(key);
                return ValueTask.FromResult<object?>(data == null ? null : new SingleRowLookup(data, tableData.index, this, connected));
            }

            throw new BifrostExecutionError("unexpected Join type: " + join.JoinName);
        }

        private static int ReadPerParentTotal(
            (IDictionary<string, int> index, IList<object?[]> data) tableData,
            JoinRowIndex joinIndex,
            object?[] key)
        {
            if (!tableData.index.TryGetValue(QueryModel.PagedKeys.Total, out var totalIdx))
                return 0;
            var row = joinIndex.First(key);
            if (row == null) return 0;
            var raw = row[totalIdx];
            return raw is null or DBNull ? 0 : Convert.ToInt32(raw);
        }

        public IEnumerator<object?> GetEnumerator()
        {
            return new ReaderEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new ReaderEnumerator(this);
        }
        public static object? DbConvert(object? val)
        {
            return val switch
            {
                DBNull => null,
                // SQL `time`/`date` columns map to the GraphQL String scalar, but
                // ADO providers hand back TimeSpan/TimeOnly/DateOnly (SQL Server
                // `time` -> TimeSpan; Npgsql `time`/`date` -> TimeOnly/DateOnly).
                // GraphQL.NET's StringGraphType.Serialize throws on non-string
                // values, so normalize these to round-trippable ISO strings here,
                // the single choke point for every read path.
                TimeSpan ts => ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                TimeOnly to => to.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                _ => val,
            };
        }

        public class ReaderEnumerator : IEnumerator<object?>, IEnumerator
        {
            private int _index = -1;
            private ReaderEnum _enum;
            private readonly int _count;

            public ReaderEnumerator(ReaderEnum @enum)
            {
                _enum = @enum;
                _count = @enum._tables[@enum._tableSql.KeyName].data.Count;
            }

            public object? Current => new ReaderCurrent(_index, (i, context) => _enum.Get(i, context));

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return ++_index < _count;
            }
            public void Reset()
            {
                _index = -1;
            }
        }
    }
    public class ReaderCurrent
    {
        private readonly int _index;
        private readonly Func<int, IBifrostFieldContext, ValueTask<object?>> _resolver;

        public ReaderCurrent(int index, Func<int, IBifrostFieldContext, ValueTask<object?>> resolver)
        {
            _index = index;
            _resolver = resolver;
        }

        public ValueTask<object?> Get(IBifrostFieldContext context)
        {
            return _resolver(_index, context);
        }
    }

    /// <summary>
    /// Extracts join-key values from a parent row using a TableJoin's
    /// FromColumns. Single-column joins yield a one-element array;
    /// composite joins yield one element per FK column.
    /// </summary>
    internal static class JoinKeyValues
    {
        /// <summary>
        /// Returns the FK key components for a parent row, or <c>null</c> when any
        /// component is NULL. A NULL FK is ordinary, valid data — the parent simply
        /// references nothing on this link — so it signals "no match" rather than
        /// throwing. Throwing here (as it once did) failed the entire query for a
        /// single nullable-FK row; the caller resolves a null single-link to null and
        /// a null multi-link to an empty collection.
        /// </summary>
        public static object?[]? FromParentRow(
            TableJoin join,
            (IDictionary<string, int> index, IList<object?[]> data) table,
            int row)
        {
            var cols = join.FromColumns;
            var values = new object?[cols.Count];
            for (var i = 0; i < cols.Count; i++)
            {
                if (!table.index.TryGetValue(cols[i], out var idx))
                    throw new BifrostExecutionError("join column not found.");
                values[i] = table.data[row][idx];
                if (values[i] == null)
                    return null;
            }
            return values;
        }
    }

    /// <summary>
    /// A structural-equality wrapper over a join-key value array so keys can be used
    /// as dictionary/lookup keys (arrays use reference equality by default).
    /// </summary>
    internal readonly struct KeyBox : IEquatable<KeyBox>
    {
        private readonly object?[] _values;
        public KeyBox(object?[] values) => _values = values;

        public bool Equals(KeyBox other)
        {
            if (_values.Length != other._values.Length) return false;
            for (var i = 0; i < _values.Length; i++)
                if (!object.Equals(_values[i], other._values[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is KeyBox other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var v in _values)
                hash.Add(v);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A prebuilt index over one join/aggregate result set: source-id key →
    /// matching rows. Built once per result set and probed per parent, turning the
    /// former O(parents × children) linear stitch (which also re-resolved the srcId
    /// column indices for every child row) into O(parents + children). Grouping
    /// preserves source row order, so per-parent child ordering is unchanged.
    /// </summary>
    internal sealed class JoinRowIndex
    {
        private readonly ILookup<KeyBox, object?[]> _byKey;
        private JoinRowIndex(ILookup<KeyBox, object?[]> byKey) => _byKey = byKey;

        public static JoinRowIndex Build(IList<object?[]> data, int[] keyIndices)
        {
            var lookup = data.ToLookup(r =>
            {
                var key = new object?[keyIndices.Length];
                for (var i = 0; i < keyIndices.Length; i++)
                    key[i] = r[keyIndices[i]];
                return new KeyBox(key);
            });
            return new JoinRowIndex(lookup);
        }

        public IList<object?[]> Match(object?[] key) => _byKey[new KeyBox(key)].ToList();

        public object?[]? First(object?[] key) => _byKey[new KeyBox(key)].FirstOrDefault();
    }

    public sealed class SubTableEnumerable : IEnumerable<object?>
    {
        private readonly (IDictionary<string, int> index, IList<object?[]> data) _table;
        private readonly IList<object?[]> _data;
        private readonly ReaderEnum _root;
        private readonly GqlObjectQuery _level;
        public SubTableEnumerable(ReaderEnum root, IList<object?[]> data, (IDictionary<string, int> index, IList<object?[]> data) @table, GqlObjectQuery level)
        {
            _root = root;
            _table = table;
            _level = level;
            // Rows are pre-matched by the caller against the shared per-result-set
            // index, so the O(children) scan no longer runs per parent.
            _data = data;
        }

        public IEnumerator<object?> GetEnumerator()
        {
            return new SubTableEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new SubTableEnumerator(this);
        }
        public ValueTask<object?> Get(int row, IBifrostFieldContext context)
        {
            var column = context.FieldName;
            var alias = context.FieldAlias;
            var lookup = alias ?? column ?? throw new BifrostExecutionError($"column name not defined");
            var found = _table.index.TryGetValue(lookup, out int index);
            if (!found)
            {
                return _root.GetDataForMissingColumn(context, _level, _table, row);
            }

            var raw = ReaderEnum.DbConvert(_data[row][index]);
            return ValueTask.FromResult(_root.MapEnumValueOrRaw(_level.DbTable.DbName, column, raw));
        }


        public sealed class SubTableEnumerator : IEnumerator<object?>, IEnumerator
        {
            private readonly SubTableEnumerable _enum;
            private int _index = -1;
            public SubTableEnumerator(SubTableEnumerable @enum)
            {
                _enum = @enum;
                _index = -1;
            }

            public object? Current => new ReaderCurrent(_index, (i, context) => _enum.Get(i, context));

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return ++_index < _enum._data.Count;
            }

            public void Reset()
            {
                _index = -1;
            }
        }
    }

    public sealed class SingleRowLookup
    {
        private readonly object?[] _row;
        private readonly IDictionary<string, int> _index;
        private readonly ReaderEnum _root;
        private readonly GqlObjectQuery _level;
        private List<object?[]>? _data;

        public SingleRowLookup(object?[] row, IDictionary<string, int> index, ReaderEnum root, GqlObjectQuery level)
        {
            _row = row;
            _index = index;
            _root = root;
            _level = level;
        }

        public ValueTask<object?> Get(IBifrostFieldContext context)
        {
            var name = context.FieldName;
            var alias = context.FieldAlias;
            if (_index.TryGetValue(alias ?? name, out var index))
                return ValueTask.FromResult(_root.MapEnumValueOrRaw(_level.DbTable.DbName, name, ReaderEnum.DbConvert(_row[index])));
            if (_index.TryGetValue(name, out var index2))
                return ValueTask.FromResult(_root.MapEnumValueOrRaw(_level.DbTable.DbName, name, ReaderEnum.DbConvert(_row[index2])));

            _data ??= new List<object?[]> { _row };

            return _root.GetDataForMissingColumn(context, _level, (_index, _data), 0);
        }
    }
}
