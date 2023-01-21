using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Linq;
using System.Security.Principal;

namespace BifrostQL.Model
{

    public interface IDbModel
    {
        IReadOnlyCollection<TableDto> Tables { get; set; }
        TableDto GetTable(string fullName);
        TableDto GetTableFromTableName(string tableName);
    }
    public sealed class DbModel : IDbModel
    {
        public IReadOnlyCollection<TableDto> Tables { get; set; } = null!;
        public TableDto GetTable(string fullName)
        {
            return Tables.First(t => t.MatchName(fullName));
        }
        public TableDto GetTableFromTableName(string tableName)
        {
            return Tables.First(t => string.Equals(t.TableName, tableName, StringComparison.InvariantCultureIgnoreCase));
        }
    }

    public sealed class TableDto
    {
        public string TableName { get; init; } = null!;
        public string GraphQLName { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableType { get; init; } = null!;
        public string FullName => $"{(TableSchema == "dbo" ? "" : $"{TableSchema}_")}{GraphQLName}";
        public string UniqueName => $"{TableSchema}.{TableName}";
        public bool MatchName(string fullName) => string.Equals(FullName, fullName, StringComparison.InvariantCultureIgnoreCase);
        public IEnumerable<ColumnDto> Columns => ColumnLookup.Values;
        public IDictionary<string, ColumnDto> ColumnLookup { get; init; } = null!;
        public Dictionary<string, TableLinkDto> SingleLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, TableLinkDto> MultiLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public IEnumerable<ColumnDto> KeyColumns => Columns.Where(c => c.IsPrimaryKey);
        public IEnumerable<ColumnDto> StandardColumns => Columns.Where(c => c.IsPrimaryKey == false);
        public override string ToString()
        {
            return $"[{TableSchema}].[{TableName}]";
        }

        public static TableDto FromReader(IDataReader reader, IReadOnlyCollection<ColumnDto>? columns = null)
        {
            var name = (string)reader["TABLE_NAME"];
            var graphQlName = name.Replace(" ", "_").Replace("-", "_");
            if (graphQlName.StartsWith("_")) graphQlName = $"tbl{graphQlName}";
            return new TableDto
            {
                TableName = name,
                GraphQLName = graphQlName,
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableType = (string)reader["TABLE_TYPE"],
                ColumnLookup = (columns ?? Array.Empty<ColumnDto>()).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
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

    public sealed class ColumnDto
    {
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public ColumnRef ColumnRef { get; init; } = null!;
        public string DataType { get; init; } = null!;
        public bool IsNullable { get; init; }
        public int OrdinalPosition { get; init; }
        public bool IsIdentity { get; init; } = false;
        public bool IsPrimaryKey { get; init; } = false;

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
            var isPrimary = constraints.TryGetValue(columnRef, out var con)
                    ? con.Any(c => c.ConstraintType == "PRIMARY KEY")
                    : false;
            return new ColumnDto
            {
                TableCatalog = catalog,
                TableSchema = schema,
                TableName = table,
                ColumnName = column,
                ColumnRef = columnRef,
                DataType = (string)reader["DATA_TYPE"],
                IsNullable = ((string)reader["IS_NULLABLE"]) == "YES",
                OrdinalPosition = (int)reader["ORDINAL_POSITION"],
                IsIdentity = (int)reader["IS_IDENTITY"] == 1,
                IsPrimaryKey = isPrimary,
            };
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

}
