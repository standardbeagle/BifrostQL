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
            var keyFound = table.index.TryGetValue(join.FromColumn, out int keyIndex);
            if (!keyFound)
                throw new BifrostExecutionError("join column not found.");

            var key = table.data[row][keyIndex];
            if (key == null)
                throw new BifrostExecutionError("key value is null");

            var tableData = _tables[join.JoinName];
            if (join.QueryType == QueryType.Join)
                return ValueTask.FromResult<object?>(new SubTableEnumerable(this, key, tableData));
            if (join.QueryType == QueryType.Single)
            {
                var srcIdIndex = tableData.index["src_id"];
                var data = tableData.data.FirstOrDefault(r => Equals(r[srcIdIndex], key));
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

    public sealed class SubTableEnumerable : IEnumerable<object?>
    {
        private readonly (IDictionary<string, int> index, IList<object?[]> data) _table;
        private readonly List<object?[]> _data;
        private readonly int _keyIndex;
        private readonly ReaderEnum _root;
        public SubTableEnumerable(ReaderEnum root, object key, (IDictionary<string, int> index, IList<object?[]> data) @table)
        {
            _root = root;
            _table = table;
            _keyIndex = table.index["src_id"];
            _data = table.data.Where(r => Equals(r[_keyIndex], key)).ToList();
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
