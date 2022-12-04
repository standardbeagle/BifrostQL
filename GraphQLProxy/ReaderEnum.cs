using GraphQL;
using GraphQL.Reflection;
using GraphQLProxy.Model;
using Microsoft.AspNetCore.Http;
using System.Collections;
using System.Data.SqlClient;
using System.Runtime.CompilerServices;
using static GraphQLProxy.DbTableResolver;

namespace GraphQLProxy
{
    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)> _tables;
        private readonly TableSqlData _tableSql;

        public ReaderEnum(TableSqlData tableSqlData, IDbConnFactory connFactory)
        {
            _tableSql = tableSqlData;
            _tables = LoadData(connFactory);
        }

        private Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)> LoadData(IDbConnFactory connFactory)
        {
            var sqlList = _tableSql.ToSql();
            var resultNames = sqlList.Keys.ToArray();
            string sql = string.Join(";", sqlList.Values);

            using var conn = connFactory.GetConnection();
            conn.Open();
            var command = new SqlCommand(sql, conn);
            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)>();
            var resultIndex = 0;
            do
            {
                var resultName = resultNames[resultIndex++];
                var index = Enumerable.Range(0, reader.FieldCount).Select(i => (i, reader.GetName(i))).ToDictionary(x => x.Item2, x => x.i, StringComparer.OrdinalIgnoreCase);
                var result = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row);
                    result.Add(row);
                }
                if (result.Count == 0)
                    results.Add(resultName, (index, new List<object?[]>()));
                else
                    results.Add(resultName, (index, result));
            } while (reader.NextResult());
            return results;
        }

        public ValueTask<object?> Get(int row, IResolveFieldContext context)
        {
            var tables = _tables;
            var table = tables[_tableSql.KeyName];
            var column = context.FieldDefinition.Name;
            var found = table.index.TryGetValue(column, out int index);
            if (!found)
            {
                var join = _tableSql.GetJoin(context.FieldAst.Alias?.Name?.StringValue, context.FieldAst.Name.StringValue);
                if (join == null)
                    throw new Exception("join not found");
                var keyFound = table.index.TryGetValue(join.ParentColumn, out int keyIndex);
                if (!keyFound)
                    throw new Exception("join column not found.");

                var key = table.data[row][keyIndex];
                if (key == null)
                    throw new Exception("key value is null");

                var fullName = $"{context.FieldAst.Alias?.Name ?? context.FieldAst.Name}+{context.FieldAst.Name}";
                var tableData = tables[fullName];
                if (context.FieldAst.Name.StringValue.StartsWith("_join_"))
                {
                    return ValueTask.FromResult<object?>(new SubTableEnumerable(this, key, column, tableData));
                }
                else
                {
                    var srcIdIndex = tableData.index["src_id"];
                    var data = tableData.data.First(r => Object.Equals(r[srcIdIndex], key));
                    return ValueTask.FromResult<object?>((tableData.index, data: data));
                }
            }
            return ValueTask.FromResult<object?>(table.data[row][index]);
        }

        public TableSqlData TableSqlData => _tableSql;

        public (Dictionary<string, int> index, List<object?[]> data) GetTableData(string name)
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
        private readonly (Dictionary<string, int> index, List<object?[]> data) _table;
        private readonly List<object?[]> _data;
        private readonly int _keyIndex;
        private readonly ReaderEnum _root;
        public SubTableEnumerable(ReaderEnum root, object key, string column, (Dictionary<string, int> index, List<object?[]> data) @table)
        {
            _root = root;
            _table = table;
            _keyIndex = table.index["src_id"];
            _data = table.data.Where(r => Object.Equals(r[_keyIndex], key)).ToList();
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
                var join = _root.TableSqlData.GetJoin(context.FieldAst.Alias?.Name?.StringValue, context.FieldAst.Name.StringValue);
                if (join == null)
                    throw new Exception("join not found");
                var keyFound = _table.index.TryGetValue(join.ParentColumn, out int keyIndex);
                if (!keyFound)
                    throw new Exception("join column not found.");

                var key = _table.data[row][keyIndex];
                if (key == null)
                    throw new Exception("key value is null");

                var fullName = $"{context.FieldAst.Alias?.Name ?? context.FieldAst.Name}+{context.FieldAst.Name}";
                var tableData = _root.GetTableData(fullName);
                    return ValueTask.FromResult<object?>(new SubTableEnumerable(_root, key, column, tableData));
            }

            return ValueTask.FromResult<object?>(_data[row][index]);
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

}
