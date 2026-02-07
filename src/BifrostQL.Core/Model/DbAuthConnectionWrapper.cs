using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Wraps a <see cref="SqlConnection"/> with database-level authentication setup
    /// and teardown based on <see cref="DbAuthConfig"/>. On open, it executes
    /// impersonation or session context SQL. On dispose, it reverts impersonation.
    /// All user-supplied values are passed as SQL parameters to prevent injection.
    /// </summary>
    public sealed class DbAuthConnectionWrapper : IDisposable
    {
        private readonly SqlConnection _connection;
        private readonly DbAuthConfig _config;
        private readonly IReadOnlyDictionary<string, string> _userContext;
        private bool _impersonationActive;
        private bool _disposed;

        public DbAuthConnectionWrapper(
            SqlConnection connection,
            DbAuthConfig config,
            IReadOnlyDictionary<string, string> userContext)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        }

        /// <summary>
        /// The underlying connection. The caller should use this for executing queries
        /// after calling <see cref="OpenWithAuthAsync"/> or <see cref="OpenWithAuth"/>.
        /// </summary>
        public SqlConnection Connection => _connection;

        /// <summary>
        /// Opens the connection and applies the configured authentication context.
        /// </summary>
        public async Task OpenWithAuthAsync()
        {
            if (_connection.State != ConnectionState.Open)
                await _connection.OpenAsync();

            await ApplyAuthContextAsync();
        }

        /// <summary>
        /// Synchronous version of <see cref="OpenWithAuthAsync"/>.
        /// </summary>
        public void OpenWithAuth()
        {
            if (_connection.State != ConnectionState.Open)
                _connection.Open();

            ApplyAuthContext();
        }

        private async Task ApplyAuthContextAsync()
        {
            switch (_config.Mode)
            {
                case DbAuthMode.SharedConnection:
                    break;

                case DbAuthMode.Impersonation:
                    await ExecuteImpersonationAsync();
                    break;

                case DbAuthMode.SessionContext:
                    await ExecuteSessionContextAsync();
                    break;

                case DbAuthMode.PerUser:
                    break;
            }
        }

        private void ApplyAuthContext()
        {
            switch (_config.Mode)
            {
                case DbAuthMode.SharedConnection:
                    break;

                case DbAuthMode.Impersonation:
                    ExecuteImpersonation();
                    break;

                case DbAuthMode.SessionContext:
                    ExecuteSessionContext();
                    break;

                case DbAuthMode.PerUser:
                    break;
            }
        }

        private async Task ExecuteImpersonationAsync()
        {
            var userName = ResolveClaimValue(_config.ImpersonationClaimKey!);
            using var cmd = new SqlCommand("EXECUTE AS USER = @userName", _connection);
            cmd.Parameters.Add(new SqlParameter("@userName", SqlDbType.NVarChar, 128) { Value = userName });
            await cmd.ExecuteNonQueryAsync();
            _impersonationActive = true;
        }

        private void ExecuteImpersonation()
        {
            var userName = ResolveClaimValue(_config.ImpersonationClaimKey!);
            using var cmd = new SqlCommand("EXECUTE AS USER = @userName", _connection);
            cmd.Parameters.Add(new SqlParameter("@userName", SqlDbType.NVarChar, 128) { Value = userName });
            cmd.ExecuteNonQuery();
            _impersonationActive = true;
        }

        private async Task ExecuteSessionContextAsync()
        {
            foreach (var mapping in _config.ClaimMappings)
            {
                var contextKey = mapping.Key;
                var claimValue = ResolveClaimValue(mapping.Value);

                using var cmd = new SqlCommand(
                    "EXEC sp_set_session_context @key, @value, @read_only",
                    _connection);
                cmd.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 128) { Value = contextKey });
                cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 4000) { Value = (object)claimValue ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@read_only", SqlDbType.Bit) { Value = true });
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private void ExecuteSessionContext()
        {
            foreach (var mapping in _config.ClaimMappings)
            {
                var contextKey = mapping.Key;
                var claimValue = ResolveClaimValue(mapping.Value);

                using var cmd = new SqlCommand(
                    "EXEC sp_set_session_context @key, @value, @read_only",
                    _connection);
                cmd.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 128) { Value = contextKey });
                cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 4000) { Value = (object)claimValue ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@read_only", SqlDbType.Bit) { Value = true });
                cmd.ExecuteNonQuery();
            }
        }

        private string ResolveClaimValue(string claimKey)
        {
            if (!_userContext.TryGetValue(claimKey, out var value))
                throw new InvalidOperationException(
                    $"Required claim '{claimKey}' not found in user context.");
            return value;
        }

        private void RevertImpersonation()
        {
            if (!_impersonationActive || _connection.State != ConnectionState.Open)
                return;

            try
            {
                using var cmd = new SqlCommand("REVERT", _connection);
                cmd.ExecuteNonQuery();
                _impersonationActive = false;
            }
            catch (SqlException)
            {
                // Connection may already be broken; swallow during cleanup.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RevertImpersonation();
            _connection.Dispose();
        }

        /// <summary>
        /// Builds a connection string for per-user mode by replacing {claim_name} placeholders
        /// in the template with resolved claim values. All replacements are validated
        /// to prevent connection string injection.
        /// </summary>
        public static string BuildPerUserConnectionString(
            string template,
            IReadOnlyDictionary<string, string> claimMappings,
            IReadOnlyDictionary<string, string> userContext)
        {
            if (string.IsNullOrWhiteSpace(template))
                throw new ArgumentException("Connection string template is required.", nameof(template));

            var result = template;
            foreach (var mapping in claimMappings)
            {
                var placeholder = $"{{{mapping.Value}}}";
                if (!result.Contains(placeholder))
                    continue;

                if (!userContext.TryGetValue(mapping.Value, out var claimValue))
                    throw new InvalidOperationException(
                        $"Required claim '{mapping.Value}' not found in user context.");

                ValidateConnectionStringValue(mapping.Value, claimValue);
                result = result.Replace(placeholder, claimValue);
            }

            var unresolvedMatch = Regex.Match(result, @"\{[^}]+\}");
            if (unresolvedMatch.Success)
                throw new InvalidOperationException(
                    $"Unresolved placeholder '{unresolvedMatch.Value}' in connection string template.");

            return result;
        }

        /// <summary>
        /// Validates that a claim value is safe for use in a connection string.
        /// Rejects values containing characters that could alter connection string semantics.
        /// </summary>
        public static void ValidateConnectionStringValue(string claimName, string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException(
                    $"Claim '{claimName}' resolved to an empty value.");

            if (value.IndexOfAny(new[] { ';', '=', '\'' }) >= 0)
                throw new ArgumentException(
                    $"Claim '{claimName}' contains characters that are not permitted in connection string values.");
        }
    }
}
