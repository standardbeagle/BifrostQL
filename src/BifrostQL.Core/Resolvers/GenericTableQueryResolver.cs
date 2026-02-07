using System.Security.Claims;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolves _table(name, limit, offset, filter) fields by querying any table and returning
    /// rows as key-value dictionaries with column metadata. Requires authentication and
    /// "generic-table: enabled" model metadata.
    /// </summary>
    public sealed class GenericTableQueryResolver : IFieldResolver
    {
        private readonly IDbModel _model;
        private readonly GenericTableConfig _config;

        public GenericTableQueryResolver(IDbModel model, GenericTableConfig config)
        {
            _model = model;
            _config = config;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var userContext = context.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            ValidateAuthorization(userContext);

            var tableName = context.GetArgument<string>("name")
                ?? throw new ExecutionError("The 'name' argument is required.");

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
            var columnMetadata = ExtractColumnMetadata(table);

            var connFactory = (IDbConnFactory)(context.InputExtensions["connFactory"]
                ?? throw new InvalidDataException("connection factory is not configured"));

            return await ExecuteQueryAsync(connFactory, table, columnMetadata, limit, offset, filter);
        }

        public void ValidateAuthorization(IDictionary<string, object?> userContext)
        {
            if (!userContext.TryGetValue("user", out var userObj))
                throw new ExecutionError("Authentication required to execute generic table queries.");

            if (userObj is not ClaimsPrincipal principal)
                throw new ExecutionError("Authentication required to execute generic table queries.");

            if (!principal.IsInRole(_config.RequiredRole) && !HasRoleClaim(principal, _config.RequiredRole))
                throw new ExecutionError($"User does not have the required role '{_config.RequiredRole}' to execute generic table queries.");
        }

        public IDbTable ResolveTable(string tableName)
        {
            if (!_config.IsTableAllowed(tableName))
                throw new ExecutionError($"Access to table '{tableName}' is not allowed.");

            try
            {
                return _model.GetTableByFullGraphQlName(tableName);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or KeyNotFoundException)
            {
                throw new ExecutionError($"Table '{tableName}' does not exist.");
            }
        }

        public static IReadOnlyList<GenericColumnMetadata> ExtractColumnMetadata(IDbTable table)
        {
            return table.Columns.Select(c => new GenericColumnMetadata
            {
                Name = c.GraphQlName,
                DataType = c.DataType,
                IsNullable = c.IsNullable,
                IsPrimaryKey = c.IsPrimaryKey,
            }).ToList();
        }

        public static (string whereSql, List<SqlParameter> parameters) BuildWhereClause(
            IDbTable table, Dictionary<string, object?>? filter)
        {
            if (filter == null || filter.Count == 0)
                return ("", new List<SqlParameter>());

            var conditions = new List<string>();
            var parameters = new List<SqlParameter>();
            var paramIndex = 0;

            foreach (var (columnName, filterValue) in filter)
            {
                if (filterValue is not Dictionary<string, object?> ops)
                    continue;

                var column = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase));
                if (column == null)
                    continue;

                foreach (var (op, value) in ops)
                {
                    var paramName = $"@gp{paramIndex++}";
                    var (condition, sqlParam) = BuildCondition(column.DbName, op, paramName, value);
                    if (condition != null)
                    {
                        conditions.Add(condition);
                        if (sqlParam != null)
                            parameters.Add(sqlParam);
                    }
                }
            }

            if (conditions.Count == 0)
                return ("", new List<SqlParameter>());

            return ($" WHERE {string.Join(" AND ", conditions)}", parameters);
        }

        private static (string? condition, SqlParameter? parameter) BuildCondition(
            string dbColumnName, string op, string paramName, object? value)
        {
            var escapedColumn = $"[{dbColumnName}]";
            return op switch
            {
                "_eq" => ($"{escapedColumn} = {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_neq" => ($"{escapedColumn} <> {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_gt" => ($"{escapedColumn} > {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_gte" => ($"{escapedColumn} >= {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_lt" => ($"{escapedColumn} < {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_lte" => ($"{escapedColumn} <= {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                "_like" => ($"{escapedColumn} LIKE {paramName}", new SqlParameter(paramName, value ?? DBNull.Value)),
                _ => (null, null),
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
            IDbTable table,
            IReadOnlyList<GenericColumnMetadata> columnMetadata,
            int limit,
            int offset,
            Dictionary<string, object?>? filter)
        {
            var (whereSql, filterParams) = BuildWhereClause(table, filter);

            var countSql = $"SELECT COUNT(*) FROM {table.DbTableRef}{whereSql}";
            var dataSql = $"SELECT * FROM {table.DbTableRef}{whereSql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();

                int totalCount;
                using (var countCmd = new SqlCommand(countSql, conn))
                {
                    foreach (var p in filterParams)
                        countCmd.Parameters.Add(CloneParameter(p));
                    var countResult = await countCmd.ExecuteScalarAsync();
                    totalCount = Convert.ToInt32(countResult);
                }

                var rows = new List<Dictionary<string, object?>>();
                using (var dataCmd = new SqlCommand(dataSql, conn))
                {
                    foreach (var p in filterParams)
                        dataCmd.Parameters.Add(CloneParameter(p));

                    using var reader = await dataCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var val = reader.GetValue(i);
                            row[reader.GetName(i)] = val == DBNull.Value ? null : val;
                        }
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
            catch (SqlException ex)
            {
                throw new ExecutionError($"Generic table query error: {ex.Message}", ex);
            }
        }

        private static SqlParameter CloneParameter(SqlParameter source)
        {
            return new SqlParameter(source.ParameterName, source.Value);
        }
    }
}
