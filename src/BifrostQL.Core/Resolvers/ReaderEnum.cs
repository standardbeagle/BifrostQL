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
            // Resolve every source-side column the join references; for
            // single-column FKs this is exactly `join.FromColumn`, for
            // composite FKs the ordered list lives on `join.FromColumns`.
            // A null in any component means the parent row is unjoinable.
            var fromColumns = join.FromColumns;
            var key = new object?[fromColumns.Count];
            for (var i = 0; i < fromColumns.Count; i++)
            {
                if (!table.index.TryGetValue(fromColumns[i], out int keyIndex))
                    throw new BifrostExecutionError("join column not found.");
                key[i] = table.data[row][keyIndex];
                if (key[i] == null)
                    throw new BifrostExecutionError("key value is null");
            }

            var tableData = _tables[join.JoinName];
            if (join.QueryType == QueryType.Join)
                return ValueTask.FromResult<object?>(new SubTableEnumerable(this, key, tableData));
            if (join.QueryType == QueryType.Single)
            {
                var data = FindRowByCompositeSrcId(tableData, key);
                return ValueTask.FromResult<object?>(data == null ? null : new SingleRowLookup(data, tableData.index, this));
            }

            throw new BifrostExecutionError("unexpected Join type: " + join.JoinName);
        }

        /// <summary>
        /// Find the row in <paramref name="tableData"/> whose `src_id`
        /// (single-column case) or `src_id_0`..`src_id_N` (composite case)
        /// matches every element of <paramref name="key"/>.
        /// </summary>
        internal static object?[]? FindRowByCompositeSrcId(
            (IDictionary<string, int> index, IList<object?[]> data) tableData,
            object?[] key)
        {
            var keyIndices = ResolveSrcIdIndices(tableData.index, key.Length);
            return tableData.data.FirstOrDefault(r => RowMatchesKey(r, keyIndices, key));
        }

        internal static int[] ResolveSrcIdIndices(IDictionary<string, int> index, int keyCount)
        {
            if (keyCount <= 1)
                return new[] { index["src_id"] };

            var result = new int[keyCount];
            for (var i = 0; i < keyCount; i++)
                result[i] = index[$"src_id_{i}"];
            return result;
        }

        internal static bool RowMatchesKey(object?[] row, int[] keyIndices, object?[] key)
        {
            for (var i = 0; i < keyIndices.Length; i++)
            {
                if (!Equals(row[keyIndices[i]], key[i]))
                    return false;
            }
            return true;
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

    public sealed class SubTableEnumerable : IEnumerable<object?>
    {
        private readonly (IDictionary<string, int> index, IList<object?[]> data) _table;
        private readonly List<object?[]> _data;
        private readonly ReaderEnum _root;
        public SubTableEnumerable(ReaderEnum root, object?[] key, (IDictionary<string, int> index, IList<object?[]> data) @table)
        {
            _root = root;
            _table = table;
            var keyIndices = ReaderEnum.ResolveSrcIdIndices(table.index, key.Length);
            _data = table.data.Where(r => ReaderEnum.RowMatchesKey(r, keyIndices, key)).ToList();
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
