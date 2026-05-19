using System.Collections;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Resolvers
{
    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> _tables;
        private readonly GqlObjectQuery _tableSql;

        public ReaderEnum(GqlObjectQuery gqlObjectQuery, IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> tableData)
        {
            _tableSql = gqlObjectQuery;
            _tables = tableData;
        }

        public ValueTask<object?> Get(int row, IBifrostFieldContext context)
        {
            var table = _tables[_tableSql.KeyName];
            var name = context.FieldName;
            var alias = context.FieldAlias;
            var found = table.index.TryGetValue(alias ?? name, out var index);
            if (found)
                return ValueTask.FromResult(DbConvert(table.data[row][index]));
            return GetDataForMissingColumn(context, table, row);
        }
        public ValueTask<object?> GetDataForMissingColumn(IBifrostFieldContext context, (IDictionary<string, int> index, IList<object?[]> data) table, int row)
        {
            string name = context.FieldName;
            string? alias = context.FieldAlias;

            var join = _tableSql.GetJoin(alias, name);
            if (join != null)
                return GetJoinResult(table, row, join);

            var aggregate = _tableSql.GetAggregate(alias, name);
            if (aggregate != null)
            {
                var tableData = _tables[aggregate.SqlKey!];
                var valueFound = tableData.index.TryGetValue(aggregate.FinalColumnGraphQlName, out int valueIndex);
                var keyFound = tableData.index.TryGetValue("srcId", out int keyIndex);
                if (!valueFound || !keyFound)
                    throw new BifrostExecutionError($"Unable to find aggregate column: {name} on table: {_tableSql.Alias}:{_tableSql.GraphQlName}");
                var parentKeyIndex = table.index[_tableSql.DbTable.KeyColumns.First().DbName];
                var parentKeyValue = table.data[row][parentKeyIndex];
                var value = tableData.data.FirstOrDefault(r => Equals(r[keyIndex], parentKeyValue))?[valueIndex];
                return ValueTask.FromResult<object?>(value);
            }
            throw new BifrostExecutionError($"Unable to find queryField: {name} on table: {_tableSql.Alias}:{_tableSql.GraphQlName}");
        }

        private ValueTask<object?> GetJoinResult((IDictionary<string, int> index, IList<object?[]> data) table, int row, TableJoin join)
        {
            var key = JoinKeyValues.FromParentRow(join, table, row);
            var tableData = _tables[join.JoinName];

            if (join.QueryType == QueryType.Join)
                return ValueTask.FromResult<object?>(new SubTableEnumerable(this, key, tableData));
            if (join.QueryType == QueryType.Single)
            {
                var data = JoinKeyMatcher.FindRow(tableData, key);
                return ValueTask.FromResult<object?>(data == null ? null : new SingleRowLookup(data, tableData.index, this));
            }

            throw new BifrostExecutionError("unexpected Join type: " + join.JoinName);
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
        public static object?[] FromParentRow(
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
                    throw new BifrostExecutionError("key value is null");
            }
            return values;
        }
    }

    /// <summary>
    /// Matches join-key value arrays against join-result rows using the
    /// <see cref="JoinKeyNames"/> alias scheme. Single source of truth for
    /// composite-key lookups — both <see cref="SubTableEnumerable"/> and
    /// <see cref="ReaderEnum.GetJoinResult"/> route through here so the
    /// single-column / composite branching never escapes this class.
    /// </summary>
    internal static class JoinKeyMatcher
    {
        public static IList<object?[]> FilterRows(
            (IDictionary<string, int> index, IList<object?[]> data) tableData,
            object?[] key) =>
            tableData.data
                .Where(r => MatchesKey(r, ResolveSrcIdIndices(tableData.index, key.Length), key))
                .ToList();

        public static object?[]? FindRow(
            (IDictionary<string, int> index, IList<object?[]> data) tableData,
            object?[] key)
        {
            var indices = ResolveSrcIdIndices(tableData.index, key.Length);
            return tableData.data.FirstOrDefault(r => MatchesKey(r, indices, key));
        }

        private static int[] ResolveSrcIdIndices(IDictionary<string, int> index, int keyCount)
        {
            var result = new int[keyCount];
            for (var i = 0; i < keyCount; i++)
                result[i] = index[JoinKeyNames.SrcIdAt(i, keyCount)];
            return result;
        }

        private static bool MatchesKey(object?[] row, int[] keyIndices, object?[] key)
        {
            for (var i = 0; i < keyIndices.Length; i++)
            {
                if (!Equals(row[keyIndices[i]], key[i]))
                    return false;
            }
            return true;
        }
    }

    public sealed class SubTableEnumerable : IEnumerable<object?>
    {
        private readonly (IDictionary<string, int> index, IList<object?[]> data) _table;
        private readonly IList<object?[]> _data;
        private readonly ReaderEnum _root;
        public SubTableEnumerable(ReaderEnum root, object?[] key, (IDictionary<string, int> index, IList<object?[]> data) @table)
        {
            _root = root;
            _table = table;
            _data = JoinKeyMatcher.FilterRows(table, key);
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
                return _root.GetDataForMissingColumn(context, _table, row);
            }

            return ValueTask.FromResult(ReaderEnum.DbConvert(_data[row][index]));
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
        private List<object?[]>? _data;

        public SingleRowLookup(object?[] row, IDictionary<string, int> index, ReaderEnum root)
        {
            _row = row;
            _index = index;
            _root = root;
        }

        public ValueTask<object?> Get(IBifrostFieldContext context)
        {
            var name = context.FieldName;
            var alias = context.FieldAlias;
            if (_index.TryGetValue(alias ?? name, out var index))
                return ValueTask.FromResult(ReaderEnum.DbConvert(_row[index]));
            if (_index.TryGetValue(name, out var index2))
                return ValueTask.FromResult(ReaderEnum.DbConvert(_row[index2]));

            _data ??= new List<object?[]> { _row };

            return _root.GetDataForMissingColumn(context, (_index, _data), 0);
        }
    }
}
