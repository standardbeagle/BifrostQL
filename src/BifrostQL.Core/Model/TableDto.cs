﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
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
}