using GraphQL;
using System.Collections;

namespace GraphQLProxy
{
    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly List<(Dictionary<string, int> index, List<object?[]> data, string name)> _tables;
        private (Dictionary<string, int> index, List<object?[]> data, string name) _table;
        private readonly Dictionary<string, (Dictionary<string, int> index, List<object?[]> data)> _tableIndex;

        public ReaderEnum(List<(Dictionary<string, int> index, List<object?[]> data, string name)> tables)
        {
            _tables = tables;
            _tableIndex = tables.ToDictionary(t => t.name, t => (t.index, t.data), StringComparer.OrdinalIgnoreCase);
            _table = _tables.First(t => t.name == "base");
        }

        public ValueTask<object?> Get(int row, IResolveFieldContext context)
        {
            var column = context.FieldDefinition.Name;
            var found = _table.index.TryGetValue(column, out int index);
            if (!found)
            {
                var fullName = $"{context.FieldAst.Alias?.Name ?? context.FieldAst.Name}+{context.FieldAst.Name}";
                var keyFound = _table.index.TryGetValue("key_" + fullName, out int keyIndex);
                if (!keyFound)
                    throw new Exception("join column not found.");

                var key = _table.data[row][keyIndex];
                if (key == null)
                    throw new Exception("key value is null");

                var tableData = _tableIndex[fullName];
                return ValueTask.FromResult<object?>(new SubTableEnumerable(key, column, tableData));
            }
            return ValueTask.FromResult<object?>(_table.data[row][index]);
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

            public ReaderEnumerator(ReaderEnum @enum)
            {
                _enum = @enum;
            }

            public object? Current => new ReaderCurrent(_index, (i, context) => _enum.Get(i, context));

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return ++_index < _enum._table.data.Count;
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
        public SubTableEnumerable(object key, string column, (Dictionary<string, int> index, List<object?[]> data) @table)
        {
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
