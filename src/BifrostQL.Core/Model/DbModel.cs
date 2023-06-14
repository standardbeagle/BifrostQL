using System.Data;
using Pluralize.NET.Core;

namespace BifrostQL.Core.Model
{

    public interface IDbModel
    {
        IReadOnlyCollection<TableDto> Tables { get; }
        string UserAuditKey { get; }
        string AuditTableName { get; }
        IDbTable GetTableByFullGraphQlName(string fullName);
        IDbTable GetTableFromDbName(string tableName);
    }

    public interface ISchemaNames
    {
        public string DbName { get; }
        public string GraphQlName { get; }
        public string NormalizedName { get; }
    }
    public sealed class DbModel : IDbModel
    {
        internal static readonly Pluralizer Pluralizer = new Pluralizer();
        public IReadOnlyCollection<TableDto> Tables { get; init; } = null!;
        public string UserAuditKey { get; init; } = null!;
        public string AuditTableName { get; init; } = null!;
        /// <summary>
        /// Searches for the table by its full graphql name
        /// </summary>
        /// <param name="fullName"></param>
        /// <returns></returns>
        public IDbTable GetTableByFullGraphQlName(string fullName)
        {
            return Tables?.FirstOrDefault(t => t.MatchName(fullName)) ?? throw new ArgumentOutOfRangeException(nameof(fullName), fullName, $"failed table lookup on graphql name: {fullName}");
        }
        public IDbTable GetTableFromDbName(string tableName)
        {
            return Tables?.FirstOrDefault(t => string.Equals(t.DbName, tableName, StringComparison.InvariantCultureIgnoreCase)) ?? throw new ArgumentOutOfRangeException(nameof(tableName), tableName, $"failed table lookup on db name: {tableName}");
        }
    }

    public interface IDbTable
    {
        /// <summary>
        /// The name of the table as it is in the database, includes spaces and special characters
        /// </summary>
        string DbName { get; init; }

        /// <summary>
        /// The name translated so that it can be used as a graphql identifier
        /// </summary>
        string GraphQlName { get; init; }

        /// <summary>
        /// The table name translated so that it can be used to predict matches from other tables and columns
        /// </summary>
        string NormalizedName { get; }

        /// <summary>
        /// The schema that the table belongs to using its database name
        /// </summary>
        string TableSchema { get; init; }

        string TableType { get; init; }

        /// <summary>
        /// The graphql name of the table, including the schema if it is not dbo
        /// </summary>
        string FullName { get; }

        IEnumerable<ColumnDto> Columns { get; }
        IDictionary<string, ColumnDto> ColumnLookup { get; init; }
        IDictionary<string, ColumnDto> GraphQlLookup { get; init; }
        IDictionary<string, TableLinkDto> SingleLinks { get; init; }
        IDictionary<string, TableLinkDto> MultiLinks { get; init; }
        IEnumerable<ColumnDto> KeyColumns { get; }
        IEnumerable<ColumnDto> StandardColumns { get; }

        bool MatchName(string fullName);
        string ToString();
    }

    public sealed class TableDto : ISchemaNames, IDbTable
    {
        /// <summary>
        /// The name of the table as it is in the database, includes spaces and special characters
        /// </summary>
        public string DbName { get; init; } = null!;
        /// <summary>
        /// The name translated so that it can be used as a graphql identifier
        /// </summary>
        public string GraphQlName { get; init; } = null!;
        /// <summary>
        /// The table name translated so that it can be used to predict matches from other tables and columns
        /// </summary>
        public string NormalizedName { get; private init; } = null!;
        /// <summary>
        /// The schema that the table belongs to using its database name
        /// </summary>
        public string TableSchema { get; init; } = null!;
        public string TableType { get; init; } = null!;
        /// <summary>
        /// The graphql name of the table, including the schema if it is not dbo
        /// </summary>
        public string FullName => $"{(TableSchema == "dbo" ? "" : $"{TableSchema}_")}{GraphQlName}";
        public bool MatchName(string fullName) => string.Equals(FullName, fullName, StringComparison.InvariantCultureIgnoreCase);
        public IEnumerable<ColumnDto> Columns => ColumnLookup.Values;
        public IDictionary<string, ColumnDto> ColumnLookup { get; init; } = null!;
        public IDictionary<string, ColumnDto> GraphQlLookup { get; init; } = null!;
        public IDictionary<string, TableLinkDto> SingleLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public IDictionary<string, TableLinkDto> MultiLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public IEnumerable<ColumnDto> KeyColumns => Columns.Where(c => c.IsPrimaryKey);
        public IEnumerable<ColumnDto> StandardColumns => Columns.Where(c => c.IsPrimaryKey == false);
        public override string ToString()
        {
            return $"[{TableSchema}].[{DbName}]";
        }

        public static TableDto FromReader(IDataReader reader, IReadOnlyCollection<ColumnDto>? columns = null)
        {
            var name = (string)reader["TABLE_NAME"];
            var graphQlName = name.ToGraphQl("tbl");
            return new TableDto
            {
                DbName = name,
                GraphQlName = graphQlName,
                NormalizedName = DbModel.Pluralizer.Singularize(name),
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableType = (string)reader["TABLE_TYPE"],
                ColumnLookup = (columns ?? Array.Empty<ColumnDto>()).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = (columns ?? Array.Empty<ColumnDto>()).ToDictionary(c => c.ColumnName.ToGraphQl("col"), StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public class TableLinkDto
    {
        public string Name { get; init; } = null!;
        public TableDto ChildTable { get; init; } = null!;
        public TableDto ParentTable { get; init; } = null!;
        public ColumnDto ChildId { get; init; } = null!;
        public ColumnDto ParentId { get; init; } = null!;
        public override string ToString() => $"{Name}-[{ChildId.TableName}.{ChildId.ColumnName}={ParentId.TableName}.{ParentId.ColumnName}]";
    }

    public record SchemaRef(string Catalog, string Schema);
    public record TableRef(string Catalog, string Schema, string Table)
        : SchemaRef(Catalog, Schema);
    public record ColumnRef(string Catalog, string Schema, string Table, string Column)
        : TableRef(Catalog, Schema, Table);

    public sealed class ColumnDto : ISchemaNames
    {
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public string DbName => ColumnName;
        public string GraphQlName { get; init; } = null!;
        public string NormalizedName { get; init; } = null!;
        public ColumnRef ColumnRef { get; init; } = null!;
        public string DataType { get; init; } = null!;
        public bool IsNullable { get; init; }
        public int OrdinalPosition { get; init; }
        public bool IsIdentity { get; init; } = false;
        public bool IsPrimaryKey { get; init; } = false;
        public bool IsCreatedOnColumn { get; set; } = false;
        public bool IsUpdatedOnColumn { get; set; } = false;
        public bool IsCreatedByColumn { get; set; } = false;
        public bool IsUpdatedByColumn { get; set; } = false;
        public Dictionary<string, bool> Flags { get; init; } = new Dictionary<string, bool>();

        public override string ToString()
        {
            return $"[{TableSchema}].[{TableName}].[{ColumnName}]({DataType} {(IsNullable ? "NULL" : "NOT NULL")}){{{(IsIdentity ? "id " : "")}{(IsPrimaryKey ? "PK " : "")}}}";
        }

        public static ColumnDto FromReader(IDataReader reader, Dictionary<ColumnRef, List<ColumnConstraintDto>> constraints)
        {
            string catalog = (string)reader["TABLE_CATALOG"];
            string schema = (string)reader["TABLE_SCHEMA"];
            string table = (string)reader["TABLE_NAME"];
            string column = (string)reader["COLUMN_NAME"];
            var columnRef = new ColumnRef(catalog, schema, table, column);
            var isPrimary = constraints.TryGetValue(columnRef, out var con) && con.Any(c => c.ConstraintType == "PRIMARY KEY");
            var graphQlName = column.ToGraphQl("col");
            return new ColumnDto
            {
                TableCatalog = catalog,
                TableSchema = schema,
                TableName = table,
                ColumnName = column,
                GraphQlName = graphQlName,
                NormalizedName = NormalizeColumn(column),
                ColumnRef = columnRef,
                DataType = (string)reader["DATA_TYPE"],
                IsNullable = ((string)reader["IS_NULLABLE"]) == "YES",
                OrdinalPosition = (int)reader["ORDINAL_POSITION"],
                IsIdentity = (int)reader["IS_IDENTITY"] == 1,
                IsPrimaryKey = isPrimary,
            };
        }

        private static string NormalizeColumn(string column)
        {
            if (string.Equals("id", column, StringComparison.InvariantCultureIgnoreCase))
                return "id";
            if (column.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
            {
                var tableName = column.Substring(0, column.Length - 2);
                return DbModel.Pluralizer.Singularize(tableName);
            }
            return column;
        }
    }

    public sealed class ColumnConstraintDto
    {
        public string ConstraintCatalog { get; init; } = null!;
        public string ConstraintSchema { get; init; } = null!;
        public string ConstraintName { get; init; } = null!;
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public string ConstraintType { get; init; } = null!;

        public static ColumnConstraintDto FromReader(IDataReader reader)
        {
            return new ColumnConstraintDto
            {
                ConstraintCatalog = (string)reader["CONSTRAINT_CATALOG"],
                ConstraintSchema = (string)reader["CONSTRAINT_SCHEMA"],
                ConstraintName = (string)reader["CONSTRAINT_NAME"],
                TableCatalog = (string)reader["TABLE_CATALOG"],
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableName = (string)reader["TABLE_NAME"],
                ColumnName = (string)reader["COLUMN_NAME"],
                ConstraintType = (string)reader["CONSTRAINT_TYPE"],
            };
        }

    }

    public static class ModelExtensions
    {
        public static string ToGraphQl(this string input, string prefix = "")
        {
            var translations = input.Select(c => c switch
            {
                ' ' => "_",
                '-' => "_",
                '_' => "_",
                >= 'a' and <= 'z' => c.ToString(),
                >= 'A' and <= 'Z' => c.ToString(),
                >= '0' and <= '9' => c.ToString(),
                _ => $"_{ ((byte)c):x}"
            });
            var result = string.Concat(translations);
            if (result[0] >= '0' && result[0] <= '9')
                result = "_" + result;
            if (result.StartsWith("_"))
                result = prefix + result;
            return result.ToLowerFirstChar();
        }

        private static string ToLowerFirstChar(this string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return char.ToLower(input[0]) + input.Substring(1);
        }
    }
}
