using System.Data;
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
        /// Searches for the table by its full graphql dbName
        /// </summary>
        /// <param dbName="fullName"></param>
        /// <returns></returns>
        public IDbTable GetTableByFullGraphQlName(string fullName)
        {
            return Tables?.FirstOrDefault(t => t.MatchName(fullName)) ?? throw new ArgumentOutOfRangeException(nameof(fullName), fullName, $"failed table lookup on graphql dbName: {fullName}");
        }
        public IDbTable GetTableFromDbName(string tableName)
        {
            return Tables?.FirstOrDefault(t => string.Equals(t.DbName, tableName, StringComparison.InvariantCultureIgnoreCase)) ?? throw new ArgumentOutOfRangeException(nameof(tableName), tableName, $"failed table lookup on db dbName: {tableName}");
        }
    }

    public interface IDbTable
    {
        /// <summary>
        /// The dbName of the table as it is in the database, includes spaces and special characters
        /// </summary>
        string DbName { get; init; }

        /// <summary>
        /// The dbName translated so that it can be used as a graphql identifier
        /// </summary>
        string GraphQlName { get; init; }

        /// <summary>
        /// The table dbName translated so that it can be used to predict matches from other tables and columns
        /// </summary>
        string NormalizedName { get; }

        /// <summary>
        /// The schema that the table belongs to using its database dbName
        /// </summary>
        string TableSchema { get; init; }

        string TableType { get; init; }

        /// <summary>
        /// The graphql dbName of the table, including the schema if it is not dbo
        /// </summary>
        string FullName { get; }

        string ColumnEnumTypeName { get; }
        string ColumnFilterTypeName { get; }
        string TableFilterTypeName { get; }
        string TableColumnSortEnumName { get;  }

        string JoinFieldName { get; }
        string SingleFieldName { get; }
        string GetJoinTypeName(IDbTable joinTable);

        IEnumerable<ColumnDto> Columns { get; }
        IDictionary<string, ColumnDto> ColumnLookup { get; init; }
        IDictionary<string, ColumnDto> GraphQlLookup { get; init; }
        IDictionary<string, TableLinkDto> SingleLinks { get; init; }
        IDictionary<string, TableLinkDto> MultiLinks { get; init; }
        IEnumerable<ColumnDto> KeyColumns { get; }

        bool MatchName(string fullName);
        string ToString();
    }

 
    public class TableLinkDto
    {
        public string Name { get; init; } = null!;
        public IDbTable ChildTable { get; init; } = null!;
        public IDbTable ParentTable { get; init; } = null!;
        public ColumnDto ChildId { get; init; } = null!;
        public ColumnDto ParentId { get; init; } = null!;
        public override string ToString() => $"{Name}-[{ChildId.TableName}.{ChildId.ColumnName}={ParentId.TableName}.{ParentId.ColumnName}]";
    }

    public record SchemaRef(string Catalog, string Schema);
    public record TableRef(string Catalog, string Schema, string Table)
        : SchemaRef(Catalog, Schema);
    public record ColumnRef(string Catalog, string Schema, string Table, string Column)
        : TableRef(Catalog, Schema, Table);


}
