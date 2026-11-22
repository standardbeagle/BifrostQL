using System.Data.Common;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolves _table(name, limit, offset, filter) fields by querying any table and returning
    /// rows as key-value dictionaries with column metadata. Requires authentication and
    /// "generic-table: enabled" model metadata.
    /// </summary>
    public sealed class GenericTableQueryResolver : IBifrostResolver, IFieldResolver
    {
        private readonly IDbModel _model;
        private readonly GenericTableConfig _config;

        public GenericTableQueryResolver(IDbModel model, GenericTableConfig config)
        {
            _model = model;
            _config = config;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            ValidateAuthorization(context.UserContext);

            var tableName = context.GetArgument<string>("name")
                ?? throw new BifrostExecutionError("The 'name' argument is required.");

            // The window obeys BOTH ceilings: the model's max-query-rows (through
            // ResolveRowWindow, like every client-reachable row read) and this surface's
            // own generic-table-max-rows. An unspecified limit asks for the surface
            // maximum; `limit: 0` is an empty page and a negative limit takes the
            // ceiling, exactly as ResolveRowWindow treats them on every other surface.
            var requestedLimit = context.HasArgument("limit")
                ? context.GetArgument<int>("limit")
                : _config.MaxRows;
            var limit = Math.Min(GqlObjectQuery.ResolveRowWindow(_model, requestedLimit), _config.MaxRows);
            var offset = context.HasArgument("offset")
                ? context.GetArgument<int>("offset")
                : 0;
            var filter = context.HasArgument("filter")
                ? context.GetArgument<Dictionary<string, object?>>("filter")
                : null;

            if (offset < 0)
                offset = 0;

            var table = ResolveTable(tableName);

            var bifrost = new BifrostContextAdapter(context);
            var connFactory = bifrost.ConnFactory;

            // The generic-table role gates ACCESS to this feature; it is not an
            // exemption from row OR column security. Run the same transformer chain
            // an ordinary table query runs: the combined tenant/soft-delete/policy
            // row filter, the column read guards over the projection, and the
            // filter/sort guards over the caller's predicates.
            // ONE seam for the whole read chain — row filter, column read guard,
            // column filter guard, crypto projection. `_table` previously hand-rolled
            // it (as three other read surfaces also did, each having drifted); see
            // TableReadChain for why the copies were collapsed.
            var chain = await TableReadChain.ForAsync(
                context.RequestServices, _model, table, context.UserContext, ReadProjection.Client);

            // Columns the caller names as predicates abort the query when denied —
            // the same reject semantics the ordinary query path uses, because a
            // predicate on an unreadable column is a value oracle regardless of
            // whether the column is projected.
            chain.AssertPredicateColumns(ResolveFilterColumns(table, filter));

            // `_table` has no client-supplied selection set to reject against, so the
            // projection is narrowed to the readable columns instead of aborting.
            var readableColumns = chain.ReadableColumns;
            if (readableColumns.Count == 0)
                throw new BifrostExecutionError($"Access to table '{table.GraphQlName}' is not allowed.");

            var columnMetadata = ExtractColumnMetadata(readableColumns);

            return await ExecuteQueryAsync(
                connFactory, _model, table, columnMetadata, readableColumns,
                limit, offset, filter, chain.RowFilter, chain);
        }

        /// <summary>
        /// DB names of the columns the caller's <c>filter</c> argument references.
        /// Unknown names are left out; <see cref="BuildFilter"/> raises its own
        /// error for those, so this never invents a column for a guard to judge.
        /// </summary>
        private static IReadOnlyList<string> ResolveFilterColumns(
            IDbTable table, Dictionary<string, object?>? filter)
        {
            if (filter == null || filter.Count == 0)
                return Array.Empty<string>();

            var names = new List<string>();
            foreach (var columnName in filter.Keys)
            {
                var column = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase));
                if (column != null)
                    names.Add(column.DbName);
            }
            return names;
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        public void ValidateAuthorization(IDictionary<string, object?> userContext)
        {
            if (!userContext.TryGetValue("user", out var userObj))
                throw new BifrostExecutionError("Authentication required to execute generic table queries.");

            if (userObj is not ClaimsPrincipal principal)
                throw new BifrostExecutionError("Authentication required to execute generic table queries.");

            if (!principal.IsInRole(_config.RequiredRole) && !HasRoleClaim(principal, _config.RequiredRole))
                throw new BifrostExecutionError($"User does not have the required role '{_config.RequiredRole}' to execute generic table queries.");
        }

        public IDbTable ResolveTable(string tableName)
        {
            if (!_config.IsTableAllowed(tableName))
                throw new BifrostExecutionError("Access to the requested table is not allowed.");

            // Positive resolve: the model's throwing lookup embeds the caller-supplied
            // name in its exception message, which would surface verbatim on the wire
            // (finding M31, protocol-adapter-security invariant 3).
            if (!_model.TryGetTableByFullGraphQlName(tableName, out var table))
                throw BifrostErrorSink.LookupMiss(
                    "The requested table does not exist.",
                    $"Generic table query table miss: '{tableName}'.",
                    nameof(GenericTableQueryResolver));

            // History targets are system tables: the generic `_table` escape hatch
            // may not read them either — it would bypass the trail field's forced
            // entity/tenant predicates and crypto image projection. Same message as
            // the allow-list denial, so the response does not leak the reason.
            if (Schema.HistorySurface.IsHistoryTarget(_model, table))
                throw new BifrostExecutionError("Access to the requested table is not allowed.");

            return table;
        }

        public static IReadOnlyList<GenericColumnMetadata> ExtractColumnMetadata(IDbTable table)
            => ExtractColumnMetadata(table.Columns);

        /// <summary>
        /// Column metadata for exactly the columns being returned. The caller must not
        /// be told about columns it may not read — the descriptor list is narrowed to
        /// the same projection the rows carry.
        /// </summary>
        public static IReadOnlyList<GenericColumnMetadata> ExtractColumnMetadata(IEnumerable<ColumnDto> columns)
        {
            return columns.Select(c => new GenericColumnMetadata
            {
                Name = c.GraphQlName,
                DataType = c.DataType,
                IsNullable = c.IsNullable,
                IsPrimaryKey = c.IsPrimaryKey,
            }).ToList();
        }

        /// <summary>
        /// The caller's <c>filter</c> argument as a <see cref="TableFilter"/>, so it renders
        /// through the same operator vocabulary, dialect parameter casts (a PostgreSQL
        /// <c>uuid</c> or <c>timestamp</c> column compared to a bound string) and NULL
        /// handling (<c>_eq: null</c> is <c>IS NULL</c>) as every other read. <c>_table</c>
        /// filters are flat: each key must name a column of this table, so no
        /// relationship traversal reaches another table past the column guards.
        /// </summary>
        public static TableFilter? BuildFilter(IDbTable table, Dictionary<string, object?>? filter)
        {
            if (filter == null || filter.Count == 0)
                return null;

            // One `{ column: { _op: value } }` term per operator, ANDed: a column with two
            // operators (`{ _gt: 1, _lt: 9 }`) is two leaf predicates, and an unknown
            // operator fails in the dialect's GetOperator instead of being dropped.
            var terms = new List<object?>();
            foreach (var (columnName, filterValue) in filter)
            {
                // A malformed filter entry must fail rather than be silently
                // skipped: dropping a predicate here widens the result set and
                // over-exposes rows the caller meant to filter out.
                if (filterValue is not Dictionary<string, object?> ops || ops.Count == 0)
                    throw new BifrostExecutionError(
                        $"Filter for '{columnName}' must be an object of operators (e.g. {{ _eq: ... }}).");

                var column = FindColumn(table, columnName)
                    ?? throw new BifrostExecutionError(
                        $"Filter references unknown column '{columnName}' on table '{table.GraphQlName}'.");
                foreach (var (op, value) in ops)
                {
                    terms.Add(new Dictionary<string, object?>
                    {
                        [column.GraphQlName] = new Dictionary<string, object?> { [op] = value },
                    });
                }
            }

            return TableFilter.FromObject(new Dictionary<string, object?> { ["and"] = terms }, table);
        }

        private static ColumnDto? FindColumn(IDbTable table, string columnName)
            => table.Columns.FirstOrDefault(c =>
                string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase));

        private static bool HasRoleClaim(ClaimsPrincipal principal, string role)
        {
            return principal.Claims.Any(c =>
                (c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<GenericTableResult> ExecuteQueryAsync(
            IDbConnFactory connFactory,
            IDbModel model,
            IDbTable table,
            IReadOnlyList<GenericColumnMetadata> columnMetadata,
            IReadOnlyList<ColumnDto> readableColumns,
            int limit,
            int offset,
            Dictionary<string, object?>? filter,
            TableFilter? securityFilter,
            TableReadChain chain)
        {
            var dialect = connFactory.Dialect;

            // AND the table's own tenant/soft-delete/policy filter onto the
            // client-supplied filter. This must never be optional out from under a
            // caller — the generic-table role only controls whether the feature is
            // reachable at all.
            //
            // A relationship-shaped security filter (policy row-scope through a
            // related table) contributes an INNER JOIN that belongs in the FROM
            // clause, plus a WHERE predicate; RenderParts returns the two apart so the
            // join is spliced before WHERE. The relationship sub-join selects DISTINCT
            // parent ids, so it narrows without multiplying rows (COUNT(*) stays accurate).
            var clientFilter = BuildFilter(table, filter);
            var combined = (clientFilter, securityFilter) switch
            {
                (null, null) => null,
                (null, _) => securityFilter,
                (_, null) => clientFilter,
                _ => TableFilter.CombineAnd(clientFilter, securityFilter),
            };
            var parameters = new SqlParameterCollection();
            var joins = "";
            var whereSql = "";
            if (combined != null)
            {
                var parts = combined.RenderParts(model, dialect, parameters, table.DbName);
                joins = parts.Joins ?? "";
                whereSql = string.IsNullOrEmpty(parts.Where) ? "" : $" WHERE {parts.Where}";
            }

            // Project the readable columns EXPLICITLY. `SELECT *` returned every column
            // including any the caller's policy denies — the column read guards can only
            // bind if the projection is derived from them. Qualify with the base table
            // name when a security join is present so the joined table's columns can't
            // widen the result either.
            var fromClause = $"{table.DbTableRef}{joins}";
            var qualifier = string.IsNullOrEmpty(joins)
                ? ""
                : $"{dialect.EscapeIdentifier(table.DbName)}.";
            var selectList = string.Join(
                ", ", readableColumns.Select(c => $"{qualifier}{dialect.EscapeIdentifier(c.DbName)}"));

            // Page in key order: LIMIT/OFFSET (or OFFSET/FETCH) without an ORDER BY lets the
            // engine return rows in any order, so consecutive pages could repeat or skip rows.
            // A keyless table has no stable order to offer, the same as the table query path.
            var keyOrder = table.KeyColumns
                .Select(c => $"{qualifier}{dialect.EscapeIdentifier(c.DbName)} asc")
                .ToList();
            var countSql = $"SELECT COUNT(*) FROM {fromClause}{whereSql}";
            var pagination = dialect.Pagination(keyOrder.Count > 0 ? keyOrder : null, offset, limit);
            var dataSql = $"SELECT {selectList} FROM {fromClause}{whereSql}{pagination}";

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();

                int totalCount;
                {
                    await using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = countSql;
                    AddParameters(countCmd, parameters);
                    var countResult = await countCmd.ExecuteScalarAsync();
                    totalCount = Convert.ToInt32(countResult);
                }

                // An empty window reads no rows. It must not reach the dialect either:
                // SQL Server rejects FETCH NEXT 0 ROWS (error 10744).
                List<Dictionary<string, object?>> rows;
                if (limit == 0)
                {
                    rows = new List<Dictionary<string, object?>>();
                }
                else
                {
                    await using var dataCmd = conn.CreateCommand();
                    dataCmd.CommandText = dataSql;
                    AddParameters(dataCmd, parameters);

                    // Rows are materialized THROUGH the chain, which is where the
                    // decrypt/mask projection lives. Reading the reader directly is
                    // what previously let envelope-encrypted columns leave as
                    // ciphertext.
                    rows = await chain.ReadRowsAsync(dataCmd);
                }

                return new GenericTableResult
                {
                    TableName = table.GraphQlName,
                    Columns = columnMetadata,
                    Rows = rows,
                    TotalCount = totalCount,
                };
            }
            catch (DbException ex)
            {
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
        }

        private static void AddParameters(DbCommand cmd, SqlParameterCollection parameters)
        {
            foreach (var parameter in parameters.Parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = parameter.Name;
                BifrostQL.Core.Resolvers.DbParameterBinder.SetValue(p, parameter.Value);
                cmd.Parameters.Add(p);
            }
        }
    }
}
