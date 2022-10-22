using GraphQL;
using GraphQL.MicrosoftDI;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQLProxy.Model;
using System.Collections;
using System.Data.SqlClient;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using static GraphQLProxy.ReaderEnum;

namespace GraphQLProxy
{
    public class DbDatabase : ObjectGraphType
    {
        private readonly IDbConnFactory _dbConnFactory;
        public DbDatabase(IReadOnlyCollection<TableDto> tables, IDbConnFactory connFactory)
        {
            Name = "database";
            _dbConnFactory = connFactory;
            foreach (var table in tables)
            {
                if (table.TableName.StartsWith("_")) continue;

                var filterArgs = table.Columns
                    .Select(column => new QueryArgument(
                        new DbFilterType(new[] { 
                            ("_eq", GraphTypeFromSql(column.DataType)),
                            ("_gt", GraphTypeFromSql(column.DataType)),
                            ("_gte", GraphTypeFromSql(column.DataType)),
                            ("_lt", GraphTypeFromSql(column.DataType)),
                            ("_lte", GraphTypeFromSql(column.DataType)),
                        })) { Name = column.ColumnName })
                    .ToList();
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "_limit" });
                filterArgs.Add(new QueryArgument<IntGraphType>() { Name = "_offset" });
                filterArgs.Add(new QueryArgument<ListGraphType<StringGraphType>>() { Name = "_order" });

                var rowType = new DbRow(table);
                AddField(new FieldType
                {
                    Name = table.TableName.Replace(" ", "__"),
                    Arguments = new QueryArguments(filterArgs),
                    ResolvedType = new ListGraphType(rowType)
                    {
                        ResolvedType = rowType
                    },
                    Resolver = new DbTableResolver(_dbConnFactory),
                });
            }
        }

        public IGraphType GraphTypeFromSql(string sqlType)
        {
            switch(sqlType)
            {
                case "float":
                case "real":
                    return new FloatGraphType();
                case "datetimeOffset":
                    return new DateTimeOffsetGraphType();
                case "datetime":
                case "datetime2":
                    return new DateTimeGraphType();
                case "bit":
                    return new BooleanGraphType();
                case "int":
                case "smallint":
                case "tinyint":
                case "money":
                case "decimal":
                    return new IntGraphType();
                case "image":
                    return new StringGraphType();
                case "varchar":
                case "nvarchar":
                case "char":
                case "nchar":
                case "binary":
                case "varbinary":
                case "ntext":
                case "text":
                    return new StringGraphType();
                default:
                    return new ObjectGraphType();
            }
        }
    }

    public class DbRow : ObjectGraphType
    {
        public DbRow(TableDto table)
        {
            Name = table.TableName.Replace(" ", "__");
            foreach(var column in table.Columns)
            {
                switch(column.DataType)
                {
                    case "int":
                    case "smallint":
                    case "tinyint":
                        Field<int>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "decimal":
                        Field<decimal>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "bigint":
                        Field<BigInteger>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "float":
                    case "real":
                        Field<double>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "datetime":
                        Field<DateTime>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "datetime2":
                        Field<DateTime>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "datetimeoffset":
                        Field<DateTimeOffset>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "bit":
                        Field<bool>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                    case "varchar":
                    case "nvarchar":
                    case "char":
                    case "nchar":
                    case "binary":
                    case "varbinary":
                    case "text":
                    case "ntext":
                    default:
                        Field<string>(column.ColumnName).Resolve(new DbFieldResolver());
                        break;
                }
            }
        }
    }

    public class DbTableResolver : IFieldResolver
    {
        private readonly IDbConnFactory _dbConnFactory;
        public DbTableResolver(IDbConnFactory connFactory)
        {
            _dbConnFactory = connFactory;
        }
        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            if (context.SubFields == null)
                throw new ArgumentNullException(nameof(context) + ".SubFields");
            var columnList = context.SubFields.Keys.Where(k => !k.StartsWith("_")).ToList();
            var columns = "[" + String.Join("],[", columnList) + "]";
            var index = columnList.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

            using var conn = _dbConnFactory.GetConnection();
            await conn.OpenAsync();

            var activeColumnArgs = (context.Arguments!).Where(arg => context.HasArgument(arg.Key) && !arg.Key.StartsWith("_")).ToArray();
            var orderby = " ORDER BY (SELECT NULL)";
            if (context.HasArgument("_order"))
            {
                var order = context.GetArgument<string[]>("_order");
                orderby = " ORDER BY " + String.Join(", ", order);
            }
            var limit = context.HasArgument("_limit") ? $" FETCH NEXT {context.GetArgument<int>("_limit")} ROWS ONLY" : "";
            var offset = context.HasArgument("_offset") ? $" OFFSET {context.GetArgument<int>("_offset")} ROWS" : " OFFSET 0 ROWS";

            var cmdText = $"SELECT {columns} FROM [{context.FieldDefinition.Name}]";
            if (activeColumnArgs.Length > 0)
            {
                cmdText += " WHERE";
                var sep = "";
                foreach(var arg in activeColumnArgs)
                {
                    var obj =(Dictionary<string, object?>)arg.Value.Value!;
                    var op = obj.First().Key;
                    var value = obj.First().Value;
                    var rel =  op switch {
                        "_eq" => "=",
                        "_neq" => "!=",
                        "_lt" => "<",
                        "_lte" => "<=",
                        "_gt" => ">",
                        "_gte" => ">=",
                        _ => "="
                    };
                    cmdText += $"{sep} [{arg.Key}] {rel} '{ value }'";
                    sep = " AND";
                }
            }
            cmdText += orderby + offset + limit;
            var command = new SqlCommand(cmdText, conn);
            using var reader = await command.ExecuteReaderAsync();
            var result = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                reader.GetValues(row);
                result.Add(row);
            }
            if (result.Count == 0) return Array.Empty<object?[]>();
            return new ReaderEnum(index, result);
        }
    }

    public class DbFieldResolver : IFieldResolver
    {
        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var row = (ReaderCurrent)context.Source!;
            return ValueTask.FromResult<object?>(row.Get(context.FieldDefinition.Name));
        }
    }

    public class ReaderEnum : IEnumerable<object?>
    {
        private readonly Dictionary<string, int> _index;
        private readonly List<object?[]> _values;

        public ReaderEnum(Dictionary<string, int> index, List<object?[]> values)
        {
            _index = index;
            _values = values;
        }

        public object? Get(int row, string column)
        {
            int index = _index[column];
            return _values[row][index];
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

            public object? Current => new ReaderCurrent(_index, _enum);

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return ++_index < _enum._values.Count;
            }

            public void Reset()
            {
                _index = -1;
            }
        }

        public class ReaderCurrent
        {
            private readonly int _index;
            private readonly ReaderEnum _enum;

            public ReaderCurrent(int index, ReaderEnum @enum)
            {
                _index = index;
                _enum = @enum;
            }

            public object? Get(string column)
            {
                return _enum.Get(_index, column);
            }
        }
    }

    public class DbFilterType : InputObjectGraphType
    {
        public DbFilterType(IEnumerable<(string fieldName, IGraphType type)> filters)
        {
            foreach(var (fieldName, type) in filters)
            {
                AddField(new FieldType
                {
                    Name = fieldName,
                    ResolvedType = type,
                });
            }
        }
    }
}
