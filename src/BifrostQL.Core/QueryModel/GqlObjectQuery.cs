using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.QueryModel
{
    public enum QueryType
    {
        Standard = 0,
        Join = 1,
        Single = 2,
        Aggregate = 3,
    }

    public sealed class GqlObjectQuery
    {
        public GqlObjectQuery() { }
        //public TableJoin? JoinFrom { get; set; }
        public IDbTable DbTable { get; init; } = null!;
        public string SchemaName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string GraphQlName { get; set; } = "";
        public string? Alias { get; set; }
        public string Path { get; set; } = "";
        public string KeyName => $"{Alias ?? GraphQlName}";
        public QueryType QueryType { get; set; }
        public List<GqlObjectColumn> ScalarColumns { get; init; } = new();
        public List<GqlAggregateColumn> AggregateColumns { get; init; } = new();

        public List<GqlObjectQuery> Links { get; set; } = new();
        public List<string> Sort { get; set; } = new();
        public TableFilter? Filter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool IsFragment { get; set; }
        public bool IncludeResult { get; set; }
        public List<TableJoin> Joins { get; set; } = new();
        public IEnumerable<TableJoin> RecurseJoins => Joins.Concat(Joins.SelectMany(j => j.ConnectedTable.RecurseJoins));

        public IEnumerable<GqlObjectColumn> FullColumnNames =>
            ScalarColumns.Where(c => c.GraphQlDbName.StartsWith("__") == false)
            .Concat(Joins.Select(j => new GqlObjectColumn(j.FromColumn)))
            .DistinctBy(c => c.GraphQlDbName, SqlNameComparer.Instance);

        public void AddSqlParameterized(IDbModel dbModel, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink? queryLink = null)
        {
            var fullColumns = FullColumnNames.ToList();
            var tableRef = dialect.TableReference(SchemaName, TableName);

            var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
            var sqlKeyName = queryLink == null ? KeyName : queryLink.Join.JoinName;

            if (fullColumns.Count > 0)
            {
                var sqlTable = dbModel.Tables.FirstOrDefault(t => t.MatchName(TableName));
                var columnSql = string.Join(",", fullColumns.Select(n =>
                {
                    var expr = dialect.EscapeIdentifier(n.DbDbName);
                    if (sqlTable != null
                        && sqlTable.ColumnLookup.TryGetValue(n.DbDbName, out var col)
                        && dialect.RequiresTextCast(col.DataType))
                        expr = dialect.TextCast(expr);
                    return $"{expr} {dialect.EscapeIdentifier(n.GraphQlDbName)}";
                }));
                var cmdText = $"SELECT {columnSql} FROM {tableRef}";

                var sortCols = Sort.Any() ? Sort.Select(s => s switch
                {
                    { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                    { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                    _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                }) : null;
                var pagination = dialect.Pagination(sortCols, Offset, Limit);

                var baseSql = new ParameterizedSql(cmdText, Array.Empty<SqlParameterInfo>())
                    .Append(filter)
                    .Append(pagination);

                sqls[sqlKeyName] = baseSql;
            }

            if (IncludeResult)
            {
                var countSql = $"SELECT COUNT(*) FROM {tableRef}";
                sqls[$"{sqlKeyName}=>count"] = new ParameterizedSql(countSql, Array.Empty<SqlParameterInfo>()).Append(filter);
            }

            foreach (var col in AggregateColumns)
            {
                var aggregateSql = col.ToSqlParameterized(dialect, filter);
                col.SqlKey = $"{sqlKeyName}=>agg_{col.FinalColumnGraphQlName}";
                sqls[col.SqlKey] = aggregateSql;
            }

            foreach (var join in Joins)
            {
                var joinQueryLink = new QueryLink(join, this, queryLink);
                AddJoinSqlParameterized(dbModel, dialect, sqls, parameters, joinQueryLink);
            }
        }

        private static void AddJoinSqlParameterized(IDbModel model, ISqlDialect dialect, IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters, QueryLink queryLink)
        {
            var main = GetRestrictedSqlParameterized(model, dialect, parameters, queryLink);
            var sql = ToConnectedSqlParameterized(model, dialect, parameters, main, queryLink.Join);
            sqls[queryLink.Join.JoinName] = sql;

            foreach (var join in queryLink.Join.ConnectedTable.Joins)
            {
                var joinQueryLink = new QueryLink(join, queryLink.Join.ConnectedTable, queryLink);
                AddJoinSqlParameterized(model, dialect, sqls, parameters, joinQueryLink);
            }
        }

        public static ParameterizedSql ToConnectedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, ParameterizedSql main, TableJoin tableJoin)
        {
            var connectedDbTable = dbModel.GetTableFromDbName(tableJoin.ConnectedTable.TableName);
            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var joinColumnSql = string.Join(",",
                tableJoin.ConnectedTable.FullColumnNames.Select(c => $"{eb}.{dialect.EscapeIdentifier(c.DbDbName)} AS {dialect.EscapeIdentifier(c.GraphQlDbName)}"));

            var (relationSql, relationParams) = tableJoin.EmitOnClause(dialect, parameters, "a", "b");
            var srcProjection = tableJoin.EmitSrcProjection(dialect, "a");

            var wrap = $"SELECT {srcProjection}, {joinColumnSql} FROM ({main.Sql}) {ea}";
            wrap += $" INNER JOIN {dialect.EscapeIdentifier(tableJoin.ConnectedTable.TableName)} {eb} ON {relationSql}";

            if (tableJoin.QueryType == QueryType.Single)
                return new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList());

            var filter = tableJoin.ConnectedTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "b");
            var sortCols = tableJoin.ConnectedTable.Sort.Any() ? tableJoin.ConnectedTable.Sort.Select(s => s switch
            {
                { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
            }) : null;
            var pagination = dialect.Pagination(sortCols, tableJoin.ConnectedTable.Offset, tableJoin.ConnectedTable.Limit);

            return new ParameterizedSql(wrap, main.Parameters.Concat(relationParams).ToList())
                .Append(filter)
                .Append(pagination);
        }

        public static ParameterizedSql GetRestrictedSqlParameterized(IDbModel dbModel, ISqlDialect dialect, SqlParameterCollection parameters, QueryLink query)
        {
            if (query.Parent == null)
            {
                var filter = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters);
                var projection = query.Join.EmitJoinIdProjection(dialect);
                var sqlText = $"SELECT DISTINCT {projection} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)}";
                var rootSql = new ParameterizedSql(sqlText, Array.Empty<SqlParameterInfo>()).Append(filter);

                // Forward the parent table's pagination into the linked
                // sub-query so the joined rows stay aligned with the paged
                // parent set. Without this, the linked SQL distinct-selects
                // the unpaged universe and the join returns rows for parents
                // outside the current page.
                //
                // Skip when the parent is unbounded — `Limit == -1` is the
                // explicit "no limit" sentinel and `Offset` of 0 (or null)
                // means "from the start", so the linked sub-query already
                // matches the parent universe.
                var hasOffset = query.FromTable.Offset.HasValue && query.FromTable.Offset.Value > 0;
                var hasLimit = query.FromTable.Limit.HasValue && query.FromTable.Limit.Value > 0;
                if (hasOffset || hasLimit)
                {
                    var sortCols = query.FromTable.Sort.Any() ? query.FromTable.Sort.Select(s => s switch
                    {
                        { } when s.EndsWith("_asc") => dialect.EscapeIdentifier(s[..^4]) + " asc",
                        { } when s.EndsWith("_desc") => dialect.EscapeIdentifier(s[..^5]) + " desc",
                        _ => throw new BifrostExecutionError($"Unsupported sort token '{s}'; expected suffix '_asc' or '_desc'.")
                    }) : null;
                    var pagination = dialect.Pagination(sortCols, query.FromTable.Offset, query.FromTable.Limit);
                    rootSql = rootSql.Append(pagination);
                }
                return rootSql;
            }

            var ea = dialect.EscapeIdentifier("a");
            var eb = dialect.EscapeIdentifier("b");
            var baseSql = GetRestrictedSqlParameterized(dbModel, dialect, parameters, query.Parent);
            var filterSql = query.FromTable.GetFilterSqlParameterized(dbModel, dialect, parameters, "a");

            // The recursive `b` sub-query exposes the parent's join-key
            // columns as JoinKeyNames.JoinIdAt — match them against this
            // layer's `a` row using the parent's ConnectedColumns.
            var (relationSql, relationParams) = query.Join.EmitOnClause(
                dialect, parameters,
                leftAlias: "b",
                rightAlias: "a",
                rightColumns: query.Parent.Join.ConnectedColumns);

            var nestedProjection = query.Join.EmitJoinIdProjection(dialect, "a");
            var querySql = $"SELECT DISTINCT {nestedProjection} FROM {dialect.EscapeIdentifier(query.FromTable.TableName)} {ea} INNER JOIN ({baseSql.Sql}) {eb} ON {relationSql}";
            return new ParameterizedSql(querySql, baseSql.Parameters.Concat(relationParams).ToList()).Append(filterSql);
        }

        /// <summary>
        /// Converts links to joins and connects them to the parent table
        /// </summary>
        /// <param name="dbModel"></param>
        /// <param name="basePath"></param>
        /// <exception cref="BifrostExecutionError"></exception>
        public void ConnectLinks(IDbModel dbModel, string basePath = "")
        {
            foreach (var link in Links)
            {
                var thisDto = dbModel.GetTableFromDbName(TableName);
                // The real query path sets FieldName (normalized) via QueryField; links
                // constructed directly carry only GraphQlName. Fall back so both resolve
                // — matching the ManyToMany branch below, which keys on GraphQlName.
                var fieldName = string.IsNullOrEmpty(link.FieldName) ? link.GraphQlName : link.FieldName;
                if (thisDto.SingleLinks.TryGetValue(fieldName, out var singleLink)
                    || (singleLink = thisDto.SingleLinks.Values.FirstOrDefault(l => string.Equals(l.ParentFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    link.TableName = singleLink.ParentTable.DbName;
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = fieldName,
                        ConnectedTable = link,
                        ConnectedColumn = singleLink.ParentId.ColumnName,
                        ConnectedColumns = singleLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        FromTable = this,
                        FromColumn = singleLink.ChildId.ColumnName,
                        FromColumns = singleLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        QueryType = QueryType.Single,
                    };
                    Joins.Add(join);
                    continue;
                }
                if (thisDto.MultiLinks.TryGetValue(fieldName, out var multiLink)
                    || (multiLink = thisDto.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                {
                    link.TableName = multiLink.ChildTable.DbName;
                    var join = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = fieldName,
                        ConnectedTable = link,
                        ConnectedColumn = multiLink.ChildId.ColumnName,
                        ConnectedColumns = multiLink.ChildIds.Select(c => c.ColumnName).ToArray(),
                        FromTable = this,
                        FromColumn = multiLink.ParentId.ColumnName,
                        FromColumns = multiLink.ParentIds.Select(c => c.ColumnName).ToArray(),
                        QueryType = QueryType.Join,
                    };
                    // Polymorphic link: constrain the child node to its discriminator
                    // value (e.g. notes.entity_type = 'company'). Applying it to the
                    // child node's Filter means the existing filter machinery emits it
                    // against the child in every SQL path — both when the child is the
                    // connected table and when it is the parent of a deeper nested join —
                    // keeping each parent's collection isolated to its own rows.
                    if (multiLink.TypePredicate is { } predicate)
                    {
                        var constFilter = TableFilter.FromObject(
                            new Dictionary<string, object?>
                            {
                                [predicate.Column.GraphQlName] =
                                    new Dictionary<string, object?> { ["_eq"] = predicate.Value }
                            },
                            multiLink.ChildTable.DbName);
                        link.Filter = link.Filter == null
                            ? constFilter
                            : new TableFilter { FilterType = FilterType.And, And = { link.Filter, constFilter } };
                    }
                    Joins.Add(join);
                    continue;
                }
                if (thisDto.ManyToManyLinks.TryGetValue(link.GraphQlName, out var m2mLink))
                {
                    // Create a junction query that selects the FK column to the target
                    var junctionQuery = new GqlObjectQuery
                    {
                        DbTable = m2mLink.JunctionTable,
                        TableName = m2mLink.JunctionTable.DbName,
                        SchemaName = m2mLink.JunctionTable.TableSchema,
                        GraphQlName = m2mLink.JunctionTable.GraphQlName,
                        Path = $"{Path}->{m2mLink.JunctionTable.GraphQlName}",
                        QueryType = QueryType.Standard,
                        ScalarColumns = { new GqlObjectColumn(m2mLink.JunctionTargetColumn.ColumnName) },
                    };
                    // First hop: source -> junction (OneToMany)
                    var junctionJoin = new TableJoin
                    {
                        Name = m2mLink.JunctionTable.GraphQlName,
                        ConnectedTable = junctionQuery,
                        ConnectedColumn = m2mLink.JunctionSourceColumn.ColumnName,
                        FromTable = this,
                        FromColumn = m2mLink.SourceColumn.ColumnName,
                        QueryType = QueryType.Join,
                    };
                    // Second hop: junction -> target (ManyToOne via junction FK)
                    link.TableName = m2mLink.TargetTable.DbName;
                    var targetJoin = new TableJoin
                    {
                        Alias = link.Alias,
                        Name = link.GraphQlName,
                        ConnectedTable = link,
                        ConnectedColumn = m2mLink.TargetColumn.ColumnName,
                        FromTable = junctionQuery,
                        FromColumn = m2mLink.JunctionTargetColumn.ColumnName,
                        QueryType = QueryType.Join,
                    };
                    junctionQuery.Joins.Add(targetJoin);
                    Joins.Add(junctionJoin);
                    continue;
                }
                throw new BifrostExecutionError($"Unable to find join {link.GraphQlName} on table {TableName}");
            }
            foreach (var join in Joins)
            {
                join.ConnectedTable.ConnectLinks(dbModel);
            }
        }

        public ParameterizedSql GetFilterSqlParameterized(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters, string? alias = null)
        {
            if (Filter == null) return ParameterizedSql.Empty;
            var result = Filter.ToSqlParameterized(model, dialect, parameters, alias);
            if (string.IsNullOrEmpty(result.Sql)) return ParameterizedSql.Empty;
            return result.Prepend(" WHERE ");
        }

        public TableJoin? GetJoin(string? alias, string name)
        {
            return RecurseJoins.FirstOrDefault(j => (alias != null && j.Alias == alias) || j.Name == name);
        }

        public GqlAggregateColumn? GetAggregate(string? alias, string name)
        {
            return AggregateColumns.FirstOrDefault(j => (alias != null && j.FinalColumnGraphQlName == alias) || j.FinalColumnGraphQlName == name);
        }

        public override string ToString()
        {
            return $"{TableName}";
        }
    }
}
