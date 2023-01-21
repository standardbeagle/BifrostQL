using GraphQL;
using BifrostQL.QueryModel;
using System;
using System.Collections;
using System.Data.SqlClient;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace BifrostQL.Resolvers
{
    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> _tables;
        private readonly TableSqlData _tableSql;

        public ReaderEnum(TableSqlData tableSqlData, IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> tableData)
        {
            _tableSql = tableSqlData;
            _tables = tableData;
        }

        public ValueTask<object?> Get(int row, IResolveFieldContext context)
        {
            var table = _tables[_tableSql.KeyName];
            var column = context.FieldDefinition.Name;
            var found = table.index.TryGetValue(column, out int index);
            if (!found)
            {
                return GetDataForMissingColumn(context, table, row);
            }
            return ValueTask.FromResult(DbConvert(table.data[row][index]));
        }
        public ValueTask<object?> GetDataForMissingColumn(IResolveFieldContext context, (IDictionary<string, int> index, IList<object?[]> data) table , int row)
        {
            string name = context.FieldAst.Name.StringValue;
            string? alias = context.FieldAst.Alias?.Name?.StringValue;

            var join = _tableSql.GetJoin(alias, name);
            if (join == null)
                throw new Exception("join not found");
            var keyFound = table.index.TryGetValue(join.FromColumn, out int keyIndex);
            if (!keyFound)
                throw new Exception("join column not found.");

            var key = table.data[row][keyIndex];
            if (key == null)
                throw new Exception("key value is null");

            //var fullName = $"{_tableSql.KeyName}->{alias ?? name}";
            var tableData = _tables[join.JoinName];
            if (join.JoinType == JoinType.Join)
                return ValueTask.FromResult<object?>(new SubTableEnumerable(this, key, tableData));
            if (join.JoinType == JoinType.Single)
            {
                var srcIdIndex = tableData.index["src_id"];
                var data = tableData.data.FirstOrDefault(r => Equals(r[srcIdIndex], key));
                return ValueTask.FromResult<object?>(data == null ? null : new SingleRowLookup(data, tableData.index, this));
            }
            throw new ArgumentOutOfRangeException("unexpected Join type:" + join.JoinName);
        }

        public TableSqlData TableSqlData => _tableSql;

        public (IDictionary<string, int> index, IList<object?[]> data) GetTableData(string name)
        {
            return _tables[name];
        }

        public IEnumerator<object?> GetEnumerator()
        {
            return new ReaderEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new ReaderEnumerator(this);
        }
        private static object? DbConvert(object? val)
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
        private readonly Func<int, IResolveFieldContext, ValueTask<object?>> _resolver;

        public ReaderCurrent(int index, Func<int, IResolveFieldContext, ValueTask<object?>> resolver)
        {
            _index = index;
            _resolver = resolver;
        }

        public ValueTask<object?> Get(IResolveFieldContext context)
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
        public ValueTask<object?> Get(int row, IResolveFieldContext context)
        {
            var column = context.FieldDefinition.Name;
            var found = _table.index.TryGetValue(column, out int index);
            if (!found)
            {
                return _root.GetDataForMissingColumn(context, _table, row);
            }

            return ValueTask.FromResult(_data[row][index]);
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

        public ValueTask<object?> Get(IResolveFieldContext context)
        {
            var name = context.FieldAst.Name.StringValue;
            if (_index.TryGetValue(name, out var index))
                return ValueTask.FromResult(_row[index]);

            _data ??= new List<object?[]> { _row };

            return _root.GetDataForMissingColumn(context, (_index, _data), 0);
        }
    }
}
