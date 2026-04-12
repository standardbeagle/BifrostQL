using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public sealed class DbTable : ISchemaNames, IDbTable
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
        public string NormalizedName { get; init; } = null!;
        /// <summary>
        /// The schema that the table belongs to using its database name
        /// </summary>
        public string TableSchema { get; init; } = null!;
        public string TableType { get; init; } = null!;
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public string? GetMetadataValue(string property) => Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) => (!Metadata.TryGetValue(property, out var v) || v?.ToString() == null) ? defaultValue : v.ToString() == "true";
        public bool CompareMetadata(string property, string value)
        {
            if (!Metadata.TryGetValue(property, out var v)) return false;
            return string.Equals(v?.ToString(), value, StringComparison.InvariantCultureIgnoreCase);
        }

        /// <summary>
        /// The graphql name of the table, including the schema if it is not dbo
        /// </summary>
        public string FullName => $"{(TableSchema == "dbo" ? "" : $"{TableSchema}_")}{GraphQlName}";

        public string ColumnEnumTypeName => $"{GraphQlName}Enum";
        public string ColumnFilterTypeName => $"FilterType{GraphQlName}EnumInput";
        public string TableFilterTypeName => $"TableFilter{GraphQlName}Input";
        public string TableColumnSortEnumName => $"{GraphQlName}SortEnum";
        public string JoinFieldName => $"_join_{GraphQlName}";
        public string SingleFieldName => $"_single_{GraphQlName}";
        public string GetJoinTypeName(IDbTable joinTable) => $"TableOn{GraphQlName}{joinTable.GraphQlName}";
        public string AggregateValueTypeName => $"{GraphQlName}_AggregateValue";
        public bool MatchName(string fullName) =>
            string.Equals(FullName, fullName, StringComparison.InvariantCultureIgnoreCase)
            || string.Equals(GraphQlName, fullName, StringComparison.InvariantCultureIgnoreCase);
        public string GetActionTypeName(MutateActions action) => $"{action}_{GraphQlName}";

        public IEnumerable<ColumnDto> Columns => ColumnLookup.Values;
        public IDictionary<string, ColumnDto> ColumnLookup { get; init; } = null!;
        public IDictionary<string, ColumnDto> GraphQlLookup { get; init; } = null!;
        public IDictionary<string, TableLinkDto> SingleLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public IDictionary<string, TableLinkDto> MultiLinks { get; init; } = new Dictionary<string, TableLinkDto>(StringComparer.InvariantCultureIgnoreCase);
        public IDictionary<string, ManyToManyLink> ManyToManyLinks { get; init; } = new Dictionary<string, ManyToManyLink>(StringComparer.InvariantCultureIgnoreCase);
        public IEnumerable<ColumnDto> KeyColumns => Columns.Where(c => c.IsPrimaryKey);
        public string DbTableRef => string.IsNullOrWhiteSpace(TableSchema) ? $"[{DbName}]" : $"[{TableSchema}].[{DbName}]";

        /// <summary>
        /// Detected column prefix groups for this table.
        /// Populated during model building when prefix-aware column matching is enabled.
        /// </summary>
        public IReadOnlyList<ColumnPrefixGroup> ColumnPrefixGroups { get; init; } = Array.Empty<ColumnPrefixGroup>();

        /// <summary>
        /// Gets columns grouped by their detected prefixes.
        /// </summary>
        public Dictionary<string, List<ColumnDto>> GetColumnsByPrefix()
        {
            var result = new Dictionary<string, List<ColumnDto>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in ColumnPrefixGroups)
            {
                result[group.Prefix] = group.Columns.ToList();
            }
            return result;
        }

        /// <summary>
        /// Checks if this table has columns with the specified prefix.
        /// </summary>
        public bool HasColumnPrefix(string prefix)
        {
            return ColumnPrefixGroups.Any(g => 
                string.Equals(g.Prefix, prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.GroupName, prefix, StringComparison.OrdinalIgnoreCase));
        }

        public override string ToString()
        {
            return $"[{TableSchema}].[{DbName}]";
        }

        public static DbTable FromReader(IDataReader reader, IReadOnlyCollection<ColumnDto>? columns = null)
        {
            var name = (string)reader["TABLE_NAME"];
            var graphQlName = name.ToGraphQl("tbl");
            var columnList = columns ?? Array.Empty<ColumnDto>();
            
            // Build GraphQlLookup using the already-computed GraphQlName on each column
            // (which has been deduplicated by ColumnDto.DeduplicateGraphQlNames)
            var graphQlLookup = new Dictionary<string, ColumnDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columnList)
            {
                if (!graphQlLookup.ContainsKey(col.GraphQlName))
                {
                    graphQlLookup[col.GraphQlName] = col;
                }
            }
            
            return new DbTable
            {
                DbName = name,
                GraphQlName = graphQlName,
                NormalizedName = DbModel.Pluralizer.Singularize(name),
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableType = (string)reader["TABLE_TYPE"],
                ColumnLookup = columnList.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = graphQlLookup,
            };
        }

        /// <summary>
        /// Returns a new DbTable with the GraphQlName prefixed according to the schema prefix options.
        /// All other properties including column lookups, links, and metadata are shared with the original.
        /// </summary>
        internal DbTable WithSchemaPrefix(SchemaPrefixOptions options)
        {
            var prefixedName = options.ApplyPrefix(GraphQlName, TableSchema);
            if (string.Equals(prefixedName, GraphQlName, StringComparison.Ordinal))
                return this;

            return new DbTable
            {
                DbName = DbName,
                GraphQlName = prefixedName,
                NormalizedName = NormalizedName,
                TableSchema = TableSchema,
                TableType = TableType,
                Metadata = Metadata,
                ColumnLookup = ColumnLookup,
                GraphQlLookup = GraphQlLookup,
                SingleLinks = SingleLinks,
                MultiLinks = MultiLinks,
                ManyToManyLinks = ManyToManyLinks,
                ColumnPrefixGroups = ColumnPrefixGroups,
            };
        }

        /// <summary>
        /// Returns a new DbTable with a modified GraphQlName.
        /// Used for deduplicating GraphQL names when multiple tables have the same name.
        /// </summary>
        internal DbTable WithGraphQlName(string newGraphQlName)
        {
            return new DbTable
            {
                DbName = DbName,
                GraphQlName = newGraphQlName,
                NormalizedName = NormalizedName,
                TableSchema = TableSchema,
                TableType = TableType,
                Metadata = Metadata,
                ColumnLookup = ColumnLookup,
                GraphQlLookup = GraphQlLookup,
                SingleLinks = SingleLinks,
                MultiLinks = MultiLinks,
                ManyToManyLinks = ManyToManyLinks,
                ColumnPrefixGroups = ColumnPrefixGroups,
            };
        }

        /// <summary>
        /// Returns a new DbTable with detected column prefix groups.
        /// </summary>
        internal DbTable WithColumnPrefixGroups(IReadOnlyList<ColumnPrefixGroup> prefixGroups)
        {
            return new DbTable
            {
                DbName = DbName,
                GraphQlName = GraphQlName,
                NormalizedName = NormalizedName,
                TableSchema = TableSchema,
                TableType = TableType,
                Metadata = Metadata,
                ColumnLookup = ColumnLookup,
                GraphQlLookup = GraphQlLookup,
                SingleLinks = SingleLinks,
                MultiLinks = MultiLinks,
                ManyToManyLinks = ManyToManyLinks,
                ColumnPrefixGroups = prefixGroups,
            };
        }
    }
}
