using BifrostQL.Core.Schema;
using BifrostQL.Core.Resolvers;
using GraphQLParser.AST;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using GraphQL.Types;

namespace BifrostQL.Core.QueryModel
{
    public sealed class GqlAggregateColumn
    {
        public GqlAggregateColumn(List<(LinkDirection direction, TableLinkDto link)> links, string name, string graphQlName, AggregateOperationType aggregateType)
        {
            Links = links;
            FinalColumnName = name;
            FinalColumnGraphQlName = graphQlName;
            AggregateType = aggregateType;
        }

        public List<(LinkDirection direction, TableLinkDto link)> Links { get; init; }
        public string FinalColumnName { get; init; }
        public string FinalColumnGraphQlName { get; init; }
        public string? SqlKey { get; set; }

        /// <summary>
        /// DbName of the column on the queried (level) table whose value keys this
        /// aggregate's result rows — i.e. the source-side join column of the first
        /// link, the same column <see cref="ToSqlParameterized"/> emits as
        /// <c>srcId</c>. ReaderEnum must probe the aggregate index with THIS
        /// column's value, not the table primary key: for a <see
        /// cref="LinkDirection.ManyToOne"/> first hop (single-link) the joined value
        /// is the child FK, which differs from the PK, so probing by PK matched the
        /// wrong rows (or none). For a <see cref="LinkDirection.OneToMany"/> first
        /// hop it is the parent's referenced key column (usually the PK).
        /// </summary>
        public string ParentKeyColumnDbName
        {
            get
            {
                if (Links.Count == 0)
                    throw new BifrostExecutionError(
                        "Aggregate correlation requires at least one link.");
                var (direction, link) = Links[0];
                return direction == LinkDirection.ManyToOne
                    ? link.ChildId.DbName
                    : link.ParentId.DbName;
            }
        }

        /// <summary>
        /// Transformer-derived WHERE filters (tenant isolation, soft-delete,
        /// authorization policy) for each linked destination table, aligned by
        /// index with <see cref="Links"/>. Populated by the query transformer
        /// service; empty when no transformers apply. Without these, the
        /// aggregate's INNER JOIN chain reads every destination row regardless
        /// of tenant/soft-delete scope — a cross-tenant data leak.
        /// </summary>
        public List<TableFilter?> LinkFilters { get; } = new();

        public AggregateOperationType? AggregateType { get; init; }

        public ParameterizedSql ToSqlParameterized(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, ParameterizedSql filterSql)
        {
            if (Links.Count == 0)
                throw new BifrostExecutionError(
                    "Aggregate value must include at least one nested-FK link (e.g. " +
                    "`value: { joinTable: { column: foo } }`); bare-column aggregates " +
                    "without a join are not supported on `_agg` inside the per-row " +
                    "`data` selection.");

            var src = dialect.EscapeIdentifier("src");
            var next = dialect.EscapeIdentifier("next");
            var joinId = dialect.EscapeIdentifier("joinId");
            var srcId = dialect.EscapeIdentifier("srcId");

            var (firstDirection, firstLink) = Links[0];
            var sql = $"SELECT {TableLinkSql.SourceColumns(firstLink, dialect, firstDirection, columnName: "joinId")}, {TableLinkSql.SourceColumns(firstLink, dialect, firstDirection, columnName: "srcId")} FROM {TableLinkSql.SourceTableRef(firstLink, dialect, firstDirection)}{filterSql.Sql}";

            var linkFilterParams = new List<SqlParameterInfo>();
            for (var i = 0; i < Links.Count; ++i)
            {
                var (direction, link) = Links[i];
                var fromSql = $" FROM ({sql}) {src} INNER JOIN {TableLinkSql.DestTableRef(link, dialect, direction)} {next} ON {src}.{joinId} = {next}.{dialect.EscapeIdentifier(TableLinkSql.DestJoinColumn(link, direction))}";
                var selectSql = (i, Links.Count - i) switch
                {
                    (_, 1) => $"SELECT {src}.{srcId}, {AggregateType}({next}.{dialect.EscapeIdentifier(FinalColumnName)}) {dialect.EscapeIdentifier(FinalColumnGraphQlName)}",
                    _ => $"SELECT {TableLinkSql.SourceColumns(link, dialect, direction, columnName: "joinId", tableName: "next")}, {src}.{srcId}",
                };
                sql = selectSql + fromSql;

                // Scope this destination table's rows by any tenant/soft-delete/
                // policy filter the transformers produced for it. The filter is
                // rendered against the `next` alias and applied at this join
                // level, so scoped rows are excluded before they propagate up the
                // aggregate chain. Security transformers emit leaf/AND equality
                // filters here, which render as plain WHERE predicates.
                var linkFilter = i < LinkFilters.Count ? LinkFilters[i] : null;
                if (linkFilter != null)
                {
                    var rendered = linkFilter.ToSqlParameterized(model, dialect, parameters, alias: "next");
                    if (!string.IsNullOrWhiteSpace(rendered.Sql))
                    {
                        sql += $" WHERE {rendered.Sql}";
                        linkFilterParams.AddRange(rendered.Parameters);
                    }
                }
            }

            return new ParameterizedSql(sql + $" GROUP BY {src}.{srcId}", filterSql.Parameters.Concat(linkFilterParams).ToList());
        }
    }
    public enum LinkDirection
    {
        OneToMany,
        ManyToOne,
    }


}
