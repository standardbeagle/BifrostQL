using System.Data;
using System.Text;
using BifrostQL.Core.QueryModel;
using Pluralize.NET.Core;

namespace BifrostQL.Core.Model
{

    public interface IDbModel
    {
        IReadOnlyCollection<IDbTable> Tables { get; }
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
        public IReadOnlyCollection<IDbTable> Tables { get; init; } = null!;
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

        string ColumnEnumTypeName { get; }
        string ColumnFilterTypeName { get; }
        string TableFilterTypeName { get; }
        string TableColumnSortEnumName { get;  }
        string JoinFieldName { get; }
        string SingleFieldName { get; }
        string GetJoinTypeName(IDbTable joinTable);
        string AggregateValueTypeName { get; }

        IEnumerable<ColumnDto> Columns { get; }
        IDictionary<string, ColumnDto> ColumnLookup { get; init; }
        IDictionary<string, ColumnDto> GraphQlLookup { get; init; }
        IDictionary<string, TableLinkDto> SingleLinks { get; init; }
        IDictionary<string, TableLinkDto> MultiLinks { get; init; }
        IEnumerable<ColumnDto> KeyColumns { get; }
        string DbTableRef { get; }

        bool MatchName(string fullName);
        string ToString();
    }

 
    public class TableLinkDto
    {
        public TableLinkDto() {}
        /// <summary>The name of the join in the scope of the table being linked from, it is context dependent. The ParentTable and ChildTable properties refer to the same tables from both sides of the link.</summary>
        public string Name { get; init; } = null!;
        /// <summary>Parent table always refers to the one in one to many relations in database joins</summary>
        public IDbTable ParentTable { get; init; } = null!;
        /// <summary>Child table always refers to the many in one to many relations in database joins</summary>
        public IDbTable ChildTable { get; init; } = null!;
        /// <summary>Parent id always refers to the one in one to many relations in database joins</summary>
        public ColumnDto ParentId { get; init; } = null!;
        /// <summary>Child id always refers to the many in one to many relations in database joins</summary>
        public ColumnDto ChildId { get; init; } = null!;

        public string GetSqlSourceTableRef(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ChildTable.DbTableRef;
            else
                return ParentTable.DbTableRef;
        }

        public string GetSqlDestTableRef(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ParentTable.DbTableRef;
            else
                return ChildTable.DbTableRef;
        }

        public string GetSqlDestJoinColumn(LinkDirection direction)
        {
            if (direction == LinkDirection.ManyToOne)
                return ParentId.DbName;
            else
                return ChildId.DbName;
        }

        public string GetSqlSourceColumns(LinkDirection direction, string tableName = null, string columnName = null)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(tableName))
                builder.Append($"[{tableName}].");
            else if (direction == LinkDirection.ManyToOne)
                builder.Append($"[{ChildTable.DbName}].");
            else
                builder.Append($"[{ParentTable.DbName}].");

            if (direction == LinkDirection.ManyToOne)
                builder.Append($"[{ ChildId.DbName }]");
            else
                builder.Append($"[{ParentId.DbName}]");

            if (!string.IsNullOrWhiteSpace(columnName))
                builder.Append($" AS [{columnName}]");

            return builder.ToString();
        }
        public override string ToString() => $"{Name}-[{ChildId.TableName}.{ChildId.ColumnName}={ParentId.TableName}.{ParentId.ColumnName}]";
    }

    public record SchemaRef(string Catalog, string Schema);
    public record TableRef(string Catalog, string Schema, string Table)
        : SchemaRef(Catalog, Schema);
    public record ColumnRef(string Catalog, string Schema, string Table, string Column)
        : TableRef(Catalog, Schema, Table);


}
