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

            var limit = context.HasArgument("limit")
                ? context.GetArgument<int>("limit")
                : _config.MaxRows;
            var offset = context.HasArgument("offset")
                ? context.GetArgument<int>("offset")
                : 0;
            var filter = context.HasArgument("filter")
                ? context.GetArgument<Dictionary<string, object?>>("filter")
                : null;

            if (limit <= 0 || limit > _config.MaxRows)
                limit = _config.MaxRows;
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
            var (securityFilter, readableColumns) = ApplyTransformers(context, table, filter);
            var columnMetadata = ExtractColumnMetadata(readableColumns);

            // Envelope-encrypted columns must never leave as ciphertext. Same
            // projector the ordinary read path builds (SqlExecutionManager), from the
            // same caller roles, so `_table` shows exactly what a normal read shows.
            var cryptoRead = new Modules.Crypto.CryptoReadProjector(
                _model,
                context.RequestServices?.GetService<BifrostQL.Core.Crypto.EnvelopeKeyManager>(),
                Auth.PolicyIdentity.ExtractRoles(context.UserContext));

            return await ExecuteQueryAsync(
                connFactory, _model, table, columnMetadata, readableColumns,
                limit, offset, filter, securityFilter, cryptoRead);
        }

        /// <summary>
        /// Runs the registered <see cref="IFilterTransformers"/> chain for this table:
        /// the combined row filter, the column read guards, and the column filter
        /// guards. Returns that filter plus the columns the caller may actually read.
        ///
        /// This resolver previously called <c>GetCombinedFilter</c> alone, so
        /// <see cref="IColumnReadGuard"/>/<see cref="IColumnFilterGuard"/> never ran on
        /// the <c>_table</c> surface: a policy-denied column came back in full, and an
        /// encrypted column could be used as a WHERE predicate. Guard decisions are
        /// delegated to the guards themselves — no second authorization rule is
        /// implemented here (protocol-adapter-security invariant 4: never reimplement
        /// authorization for a second surface, call the same evaluator).
        ///
        /// Returns null/all-columns when no transformer pipeline is registered, matching
        /// how the rest of the read path degrades in lightweight hosts.
        /// </summary>
        private (TableFilter? SecurityFilter, IReadOnlyList<ColumnDto> ReadableColumns) ApplyTransformers(
            IBifrostFieldContext context, IDbTable table, Dictionary<string, object?>? filter)
        {
            var filterTransformers = context.RequestServices?.GetService<IFilterTransformers>();
            if (filterTransformers == null)
                return (null, table.Columns.ToList());

            var transformContext = new QueryTransformContext
            {
                Model = _model,
                UserContext = context.UserContext,
                QueryType = QueryType.Standard,
                Path = table.GraphQlName,
                IsNestedQuery = false,
            };

            // Columns the caller names as predicates abort the query when denied —
            // the same reject semantics the ordinary query path uses, because a
            // predicate on an unreadable column is a value oracle regardless of
            // whether the column is projected.
            var predicateColumns = ResolveFilterColumns(table, filter);
            if (predicateColumns.Count > 0)
            {
                foreach (var guard in filterTransformers.OfType<IColumnReadGuard>())
                    guard.AssertColumnsReadable(table, predicateColumns, transformContext);
                foreach (var guard in filterTransformers.OfType<IColumnFilterGuard>())
                    guard.AssertColumnsFilterable(table, predicateColumns, transformContext);
            }

            // `_table` has no client-supplied selection set to reject against, so the
            // projection is narrowed to the readable columns instead of aborting.
            var readable = SelectReadableColumns(filterTransformers, table, transformContext);
            if (readable.Count == 0)
                throw new BifrostExecutionError($"Access to table '{table.GraphQlName}' is not allowed.");

            return (filterTransformers.GetCombinedFilter(table, transformContext), readable);
        }

        /// <summary>
        /// DB names of the columns the caller's <c>filter</c> argument references.
        /// Unknown names are left out; <see cref="BuildWhereClause"/> raises its own
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

        /// <summary>
        /// The subset of <paramref name="table"/>'s columns the caller may read, decided
        /// entirely by the registered <see cref="IColumnReadGuard"/>s: the whole set is
        /// offered first, and only if that is rejected is each column offered on its own
        /// to find which ones are denied. A guard's throw excludes the column —
        /// fail-closed, and the guard stays the single authority on the decision.
        /// </summary>
        private static IReadOnlyList<ColumnDto> SelectReadableColumns(
            IFilterTransformers filterTransformers, IDbTable table, QueryTransformContext transformContext)
        {
            var guards = filterTransformers.OfType<IColumnReadGuard>().ToArray();
            if (guards.Length == 0)
                return table.Columns.ToList();

            // Fast path: ask about the whole table once. A table with no column denies
            // — the common case — costs one guard call instead of one per column, and
            // the per-column probing below only runs when something is actually denied.
            var allColumns = table.Columns.ToList();
            if (AllReadable(guards, table, allColumns.Select(c => c.DbName).ToArray(), transformContext))
                return allColumns;

            var readable = new List<ColumnDto>();
            foreach (var column in allColumns)
            {
                if (AllReadable(guards, table, new[] { column.DbName }, transformContext))
                    readable.Add(column);
            }

            return readable;
        }

        /// <summary>
        /// True when every registered guard accepts <paramref name="columns"/>. A guard
        /// rejects by throwing, so any <see cref="BifrostExecutionError"/> means "not
        /// readable" — the guard remains the only thing deciding.
        /// </summary>
        private static bool AllReadable(
            IReadOnlyList<IColumnReadGuard> guards,
            IDbTable table,
            string[] columns,
            QueryTransformContext transformContext)
        {
            foreach (var guard in guards)
            {
                try
                {
                    guard.AssertColumnsReadable(table, columns, transformContext);
                }
                catch (BifrostExecutionError)
                {
                    return false;
                }
            }

            return true;
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
                throw new BifrostExecutionError($"Access to table '{tableName}' is not allowed.");

            IDbTable table;
            try
            {
                table = _model.GetTableByFullGraphQlName(tableName);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or KeyNotFoundException)
            {
                throw new BifrostExecutionError($"Table '{tableName}' does not exist.");
            }

            // History targets are system tables: the generic `_table` escape hatch
            // may not read them either — it would bypass the trail field's forced
            // entity/tenant predicates and crypto image projection. Same message as
            // the allow-list denial, so the response does not leak the reason.
            if (Schema.HistorySurface.IsHistoryTarget(_model, table))
                throw new BifrostExecutionError($"Access to table '{tableName}' is not allowed.");

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

        public static (string whereSql, List<(string name, object? value)> parameters) BuildWhereClause(
            IDbTable table, ISqlDialect dialect, Dictionary<string, object?>? filter)
        {
            if (filter == null || filter.Count == 0)
                return ("", new List<(string, object?)>());

            var conditions = new List<string>();
            var parameters = new List<(string name, object? value)>();
            var paramIndex = 0;

            foreach (var (columnName, filterValue) in filter)
            {
                // A malformed filter entry must fail rather than be silently
                // skipped: dropping a predicate here widens the result set and
                // over-exposes rows the caller meant to filter out.
                if (filterValue is not Dictionary<string, object?> ops)
                    throw new BifrostExecutionError(
                        $"Filter for '{columnName}' must be an object of operators (e.g. {{ _eq: ... }}).");

                var column = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase));
                if (column == null)
                    throw new BifrostExecutionError(
                        $"Filter references unknown column '{columnName}' on table '{table.GraphQlName}'.");

                foreach (var (op, value) in ops)
                {
                    var paramName = $"@gp{paramIndex++}";
                    var (condition, param) = BuildCondition(dialect, column.DbName, op, paramName, value);
                    conditions.Add(condition);
                    if (param != null)
                        parameters.Add(param.Value);
                }
            }

            if (conditions.Count == 0)
                return ("", new List<(string, object?)>());

            return ($" WHERE {string.Join(" AND ", conditions)}", parameters);
        }

        private static (string condition, (string name, object? value)? parameter) BuildCondition(
            ISqlDialect dialect, string dbColumnName, string op, string paramName, object? value)
        {
            var escapedColumn = dialect.EscapeIdentifier(dbColumnName);
            return op switch
            {
                "_eq" => ($"{escapedColumn} = {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_neq" => ($"{escapedColumn} <> {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_gt" => ($"{escapedColumn} > {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_gte" => ($"{escapedColumn} >= {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_lt" => ($"{escapedColumn} < {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_lte" => ($"{escapedColumn} <= {paramName}", (paramName, value ?? (object)DBNull.Value)),
                "_like" => ($"{escapedColumn} LIKE {paramName}", (paramName, value ?? (object)DBNull.Value)),
                // An unrecognized operator must throw, not silently drop the
                // predicate — the same fail-fast rule as SqlDialectBase.GetOperator.
                _ => throw new BifrostExecutionError(
                    $"Unsupported filter operator '{op}'. Valid operators: " +
                    $"_eq, _neq, _gt, _gte, _lt, _lte, _like."),
            };
        }

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
            Modules.Crypto.CryptoReadProjector cryptoRead)
        {
            var dialect = connFactory.Dialect;
            var (whereSql, filterParams) = BuildWhereClause(table, dialect, filter);

            // AND the table's own tenant/soft-delete/policy filter onto the
            // client-supplied WHERE. This must never be optional out from under a
            // caller — the generic-table role only controls whether the feature is
            // reachable at all.
            //
            // A relationship-shaped security filter (policy row-scope through a
            // related table) contributes an INNER JOIN that belongs in the FROM
            // clause, plus a WHERE predicate. Render the two parts separately so the
            // join is spliced before WHERE — wrapping the whole rendering in
            // `WHERE (...)` produced `WHERE ( INNER JOIN ... )`, invalid SQL. The
            // relationship sub-join selects DISTINCT parent ids, so it narrows without
            // multiplying rows (COUNT(*) stays accurate).
            var securityParams = new SqlParameterCollection();
            string securityJoins = "";
            string securityWhere = "";
            if (securityFilter != null)
            {
                var parts = securityFilter.RenderParts(model, dialect, securityParams, table.DbName);
                securityJoins = parts.Joins ?? "";
                securityWhere = parts.Where ?? "";
            }

            var combinedWhereSql = (whereSql, securityWhere) switch
            {
                ("", "") => "",
                ("", _) => $" WHERE ({securityWhere})",
                (_, "") => whereSql,
                _ => $"{whereSql} AND ({securityWhere})",
            };

            // Project the readable columns EXPLICITLY. `SELECT *` returned every column
            // including any the caller's policy denies — the column read guards can only
            // bind if the projection is derived from them. Qualify with the base table
            // name when a security join is present so the joined table's columns can't
            // widen the result either.
            var fromClause = $"{table.DbTableRef}{securityJoins}";
            var qualifier = string.IsNullOrEmpty(securityJoins)
                ? ""
                : $"{dialect.EscapeIdentifier(table.DbName)}.";
            var selectList = string.Join(
                ", ", readableColumns.Select(c => $"{qualifier}{dialect.EscapeIdentifier(c.DbName)}"));

            var countSql = $"SELECT COUNT(*) FROM {fromClause}{combinedWhereSql}";
            var pagination = dialect.Pagination(null, offset, limit);
            var dataSql = $"SELECT {selectList} FROM {fromClause}{combinedWhereSql}{pagination}";

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();

                int totalCount;
                {
                    await using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = countSql;
                    AddFilterParameters(countCmd, filterParams);
                    AddSecurityParameters(countCmd, securityParams);
                    var countResult = await countCmd.ExecuteScalarAsync();
                    totalCount = Convert.ToInt32(countResult);
                }

                var rows = new List<Dictionary<string, object?>>();
                {
                    await using var dataCmd = conn.CreateCommand();
                    dataCmd.CommandText = dataSql;
                    AddFilterParameters(dataCmd, filterParams);
                    AddSecurityParameters(dataCmd, securityParams);

                    await using var reader = await dataCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var row = DbReaderExtensions.ReadRow(reader);
                        // Decrypt/mask encrypted columns exactly as the ordinary read
                        // path does. Non-encrypted columns pass through untouched.
                        foreach (var key in row.Keys.ToArray())
                            row[key] = cryptoRead.Project(table.DbName, key, row[key]);
                        rows.Add(row);
                    }
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
                throw new BifrostExecutionError($"Generic table query error: {ex.Message}", ex);
            }
        }

        private static void AddFilterParameters(DbCommand cmd, List<(string name, object? value)> filterParams)
        {
            foreach (var (name, value) in filterParams)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        private static void AddSecurityParameters(DbCommand cmd, SqlParameterCollection securityParams)
        {
            foreach (var parameter in securityParams.Parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = parameter.Name;
                p.Value = parameter.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }
    }
}
