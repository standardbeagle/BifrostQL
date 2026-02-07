using System.Security.Claims;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolves _rawQuery fields by executing parameterized SELECT queries against the database.
    /// Requires authentication and the "raw-sql: enabled" model metadata setting.
    /// </summary>
    public sealed class RawSqlQueryResolver : IFieldResolver
    {
        public const string MetadataKey = "raw-sql";
        public const string MetadataEnabled = "enabled";
        public const string DefaultRequiredRole = "bifrost-raw-sql";
        public const int DefaultTimeoutSeconds = 30;
        public const int DefaultMaxRows = 1000;
        public const string RoleMetadataKey = "raw-sql-role";
        public const string TimeoutMetadataKey = "raw-sql-timeout";
        public const string MaxRowsMetadataKey = "raw-sql-max-rows";

        private readonly IDbModel _model;
        private readonly RawSqlValidator _validator = new();

        public RawSqlQueryResolver(IDbModel model)
        {
            _model = model;
        }

        public async ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var userContext = context.UserContext as IDictionary<string, object?> ?? new Dictionary<string, object?>();
            ValidateAuthorization(userContext);

            var sql = context.GetArgument<string>("sql");
            var paramsArg = context.HasArgument("params")
                ? context.GetArgument<Dictionary<string, object?>>("params")
                : null;
            var timeout = context.HasArgument("timeout")
                ? context.GetArgument<int>("timeout")
                : GetConfiguredTimeout();

            var validationResult = _validator.Validate(sql);
            if (!validationResult.IsValid)
                throw new ExecutionError($"SQL validation failed: {validationResult.ErrorMessage}");

            var maxTimeout = GetConfiguredTimeout();
            if (timeout > maxTimeout)
                timeout = maxTimeout;
            if (timeout <= 0)
                timeout = DefaultTimeoutSeconds;

            var maxRows = GetConfiguredMaxRows();

            var conFactory = (IDbConnFactory)(context.InputExtensions["connFactory"]
                ?? throw new InvalidDataException("connection factory is not configured"));

            return await ExecuteQueryAsync(conFactory, sql, paramsArg, timeout, maxRows);
        }

        private void ValidateAuthorization(IDictionary<string, object?> userContext)
        {
            var requiredRole = _model.GetMetadataValue(RoleMetadataKey) ?? DefaultRequiredRole;

            if (!userContext.TryGetValue("user", out var userObj))
                throw new ExecutionError("Authentication required to execute raw SQL queries.");

            if (userObj is not ClaimsPrincipal principal)
                throw new ExecutionError("Authentication required to execute raw SQL queries.");

            if (!principal.IsInRole(requiredRole) && !HasRoleClaim(principal, requiredRole))
                throw new ExecutionError($"User does not have the required role '{requiredRole}' to execute raw SQL queries.");
        }

        private static bool HasRoleClaim(ClaimsPrincipal principal, string role)
        {
            return principal.Claims.Any(c =>
                (c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));
        }

        private int GetConfiguredTimeout()
        {
            var value = _model.GetMetadataValue(TimeoutMetadataKey);
            return int.TryParse(value, out var timeout) && timeout > 0 ? timeout : DefaultTimeoutSeconds;
        }

        private int GetConfiguredMaxRows()
        {
            var value = _model.GetMetadataValue(MaxRowsMetadataKey);
            return int.TryParse(value, out var maxRows) && maxRows > 0 ? maxRows : DefaultMaxRows;
        }

        private static async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(
            IDbConnFactory connFactory, string sql, Dictionary<string, object?>? parameters, int timeoutSeconds, int maxRows)
        {
            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn)
                {
                    CommandTimeout = timeoutSeconds
                };

                if (parameters != null)
                {
                    foreach (var (name, value) in parameters)
                    {
                        var paramName = name.StartsWith("@") ? name : $"@{name}";
                        cmd.Parameters.Add(new SqlParameter(paramName, value ?? DBNull.Value));
                    }
                }

                var results = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                var rowCount = 0;
                while (await reader.ReadAsync() && rowCount < maxRows)
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var val = reader.GetValue(i);
                        row[reader.GetName(i)] = val == DBNull.Value ? null : val;
                    }
                    results.Add(row);
                    rowCount++;
                }

                return results;
            }
            catch (SqlException ex)
            {
                throw new ExecutionError($"SQL execution error: {ex.Message}", ex);
            }
        }
    }
}
