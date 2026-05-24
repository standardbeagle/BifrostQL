using System.Data.Common;
using System.Security.Claims;
using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Resolvers;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolves _rawQuery fields by executing parameterized SELECT queries against the database.
    /// Requires authentication and the "raw-sql: enabled" model metadata setting.
    /// </summary>
    public sealed class RawSqlQueryResolver : IBifrostResolver, IFieldResolver
    {
        public const string MetadataKey = MetadataKeys.RawSql.Enabled;
        public const string MetadataEnabled = "enabled";
        public const string DefaultRequiredRole = "bifrost-raw-sql";
        public const int DefaultTimeoutSeconds = 30;
        public const int DefaultMaxRows = 1000;
        public const string RoleMetadataKey = MetadataKeys.RawSql.Role;
        public const string TimeoutMetadataKey = MetadataKeys.RawSql.Timeout;
        public const string MaxRowsMetadataKey = MetadataKeys.RawSql.MaxRows;

        private readonly IDbModel _model;
        private readonly RawSqlValidator _validator = new();

        public RawSqlQueryResolver(IDbModel model)
        {
            _model = model;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            ValidateAuthorization(context.UserContext);

            var sql = context.GetArgument<string>("sql") ?? throw new BifrostExecutionError("SQL query argument is required");
            var paramsArg = context.HasArgument("params")
                ? context.GetArgument<Dictionary<string, object?>>("params")
                : null;
            var timeout = context.HasArgument("timeout")
                ? context.GetArgument<int>("timeout")
                : GetConfiguredTimeout();

            var validationResult = _validator.Validate(sql);
            if (!validationResult.IsValid)
                throw new BifrostExecutionError($"SQL validation failed: {validationResult.ErrorMessage}");

            var maxTimeout = GetConfiguredTimeout();
            if (timeout > maxTimeout)
                timeout = maxTimeout;
            if (timeout <= 0)
                timeout = DefaultTimeoutSeconds;

            var maxRows = GetConfiguredMaxRows();

            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;

            return await ExecuteQueryAsync(conFactory, sql, paramsArg, timeout, maxRows);
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        private void ValidateAuthorization(IDictionary<string, object?> userContext)
        {
            var requiredRole = _model.GetMetadataValue(RoleMetadataKey) ?? DefaultRequiredRole;

            if (!userContext.TryGetValue("user", out var userObj))
                throw new BifrostExecutionError("Authentication required to execute raw SQL queries.");

            if (userObj is not ClaimsPrincipal principal)
                throw new BifrostExecutionError("Authentication required to execute raw SQL queries.");

            if (!principal.IsInRole(requiredRole) && !HasRoleClaim(principal, requiredRole))
                throw new BifrostExecutionError($"User does not have the required role '{requiredRole}' to execute raw SQL queries.");
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
            try
            {
                // Shared with the Photino SQL bridge. The executor returns a columnar
                // result; the GraphQL field shape is a list of name-keyed rows, so we
                // adapt here. Duplicate column names collapse (last wins) — unchanged
                // from the original dictionary-based behavior.
                var result = await RawSqlExecutor.ExecuteAsync(
                    connFactory, sql, parameters, timeoutSeconds, maxRows);

                var results = new List<Dictionary<string, object?>>(result.Rows.Count);
                foreach (var row in result.Rows)
                {
                    var dict = new Dictionary<string, object?>(result.Columns.Count);
                    for (var i = 0; i < result.Columns.Count; i++)
                        dict[result.Columns[i].Name] = row[i];
                    results.Add(dict);
                }

                return results;
            }
            catch (DbException ex)
            {
                throw new BifrostExecutionError($"SQL execution error: {ex.Message}", ex);
            }
        }
    }
}
