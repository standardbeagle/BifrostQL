using System.Data;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Resolver for stored procedure execution.
    /// Handles input/output parameters and multiple result sets.
    /// </summary>
    public sealed class StoredProcedureResolver : DatabaseResolverBase
    {
        /// <summary>
        /// Model-level role required to execute any stored procedure. Unlike
        /// <c>_rawQuery</c> (<c>raw-sql-role</c>) and <c>_table</c>
        /// (<c>generic-table-role</c>), stored procedures had no authorization
        /// gate at all — any exposed proc was callable with zero role check.
        /// This is a local literal, not <c>MetadataKeys.StoredProcedures</c>,
        /// because that class is owned by another workstream; promoting this key
        /// into <c>MetadataKeys</c> (plus wiring it into the metadata
        /// validation allow-list alongside <c>sp-include</c>/<c>sp-exclude</c>)
        /// is a required follow-up. Filter transformers cannot rewrite an
        /// arbitrary proc body, so this is an auth gate only — no row-level
        /// tenant scoping is implied or attempted here.
        /// </summary>
        public const string RoleMetadataKey = "stored-procedure-role";

        private readonly DbStoredProcedure _proc;

        public StoredProcedureResolver(DbStoredProcedure proc)
        {
            _proc = proc ?? throw new ArgumentNullException(nameof(proc));
        }

        public override async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;

            ValidateAuthorization(context.UserContext, bifrost.Model);

            var input = context.HasArgument("input")
                ? context.GetArgument<Dictionary<string, object?>>("input")
                : null;

            await using var conn = conFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = _proc.FullDbRef;
                cmd.CommandType = CommandType.StoredProcedure;

                AddInputParameters(cmd, input);
                var outputParams = AddOutputParameters(cmd);

                var resultSets = new List<List<Dictionary<string, object?>>>();
                int affectedRows;

                await using var reader = await cmd.ExecuteReaderAsync();
                do
                {
                    var resultSet = new List<Dictionary<string, object?>>();
                    while (await reader.ReadAsync())
                    {
                        resultSet.Add(DbReaderExtensions.ReadRow(reader));
                    }
                    if (resultSet.Count > 0 || reader.FieldCount > 0)
                        resultSets.Add(resultSet);
                } while (await reader.NextResultAsync());

                affectedRows = reader.RecordsAffected >= 0 ? reader.RecordsAffected : 0;

                var result = new Dictionary<string, object?>
                {
                    ["resultSets"] = resultSets,
                    ["affectedRows"] = affectedRows,
                };

                foreach (var (paramName, dbParam) in outputParams)
                {
                    result[paramName] = dbParam.Value == DBNull.Value ? null : dbParam.Value;
                }

                return result;
            }
            catch (Exception ex)
            {
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
        }

        /// <summary>
        /// Requires the caller to hold <see cref="RoleMetadataKey"/>'s configured
        /// role when the model configures one. When no role is configured, this
        /// mirrors pre-fix behavior (no gate) — see the follow-up note on
        /// <see cref="RoleMetadataKey"/> about promoting this to a first-class,
        /// always-enforced metadata key.
        /// </summary>
        private static void ValidateAuthorization(IDictionary<string, object?> userContext, IDbModel model)
        {
            var requiredRole = model.GetMetadataValue(RoleMetadataKey);
            if (string.IsNullOrWhiteSpace(requiredRole))
                return;

            if (!userContext.TryGetValue("user", out var userObj))
                throw new BifrostExecutionError("Authentication required to execute this stored procedure.");

            if (userObj is not ClaimsPrincipal principal)
                throw new BifrostExecutionError("Authentication required to execute this stored procedure.");

            if (!principal.IsInRole(requiredRole) && !HasRoleClaim(principal, requiredRole))
                throw new BifrostExecutionError(
                    $"User does not have the required role '{requiredRole}' to execute this stored procedure.");
        }

        private static bool HasRoleClaim(ClaimsPrincipal principal, string role)
        {
            return principal.Claims.Any(c =>
                (c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
                && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));
        }

        private void AddInputParameters(System.Data.Common.DbCommand cmd, Dictionary<string, object?>? input)
        {
            foreach (var param in _proc.InputParameters)
            {
                object? value = null;
                input?.TryGetValue(param.GraphQlName, out value);

                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = $"@{param.DbName}";
                dbParam.Value = value ?? DBNull.Value;
                dbParam.Direction = param.Direction == ParameterDirection.InputOutput
                    ? ParameterDirection.InputOutput
                    : ParameterDirection.Input;
                cmd.Parameters.Add(dbParam);
            }
        }

        private List<(string paramName, System.Data.Common.DbParameter dbParam)> AddOutputParameters(System.Data.Common.DbCommand cmd)
        {
            var outputParams = new List<(string, System.Data.Common.DbParameter)>();

            foreach (var param in _proc.OutputParameters)
            {
                if (param.Direction == ParameterDirection.InputOutput)
                {
                    var existing = cmd.Parameters[$"@{param.DbName}"];
                    outputParams.Add((param.GraphQlName, (System.Data.Common.DbParameter)existing!));
                    continue;
                }

                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = $"@{param.DbName}";
                dbParam.Direction = param.Direction;
                dbParam.Value = DBNull.Value;
                dbParam.Size = GetDefaultSize(param.DataType);
                cmd.Parameters.Add(dbParam);
                outputParams.Add((param.GraphQlName, dbParam));
            }

            return outputParams;
        }

        private static int GetDefaultSize(string dataType)
        {
            var normalized = StringNormalizer.NormalizeType(dataType);
            return normalized switch
            {
                "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => 4000,
                "varbinary" or "binary" => 8000,
                _ => 0,
            };
        }
    }
}
