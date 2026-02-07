namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Specifies how BifrostQL authenticates to the database on behalf of the user.
    /// </summary>
    public enum DbAuthMode
    {
        /// <summary>
        /// All requests share a single connection string. No per-user database identity.
        /// This is the default mode.
        /// </summary>
        SharedConnection,

        /// <summary>
        /// Uses SQL Server's EXECUTE AS USER to impersonate the database user mapped
        /// from a JWT claim. The connection reverts impersonation on dispose.
        /// </summary>
        Impersonation,

        /// <summary>
        /// Sets SQL Server SESSION_CONTEXT values from JWT claims so that row-level
        /// security policies can reference the user context.
        /// </summary>
        SessionContext,

        /// <summary>
        /// Builds a separate connection string per user from a template, replacing
        /// placeholders with JWT claim values. Each user gets their own connection pool.
        /// </summary>
        PerUser,
    }

    /// <summary>
    /// Configuration for database-level authentication. Maps JWT claims to
    /// database user context using the selected <see cref="DbAuthMode"/>.
    /// </summary>
    public sealed class DbAuthConfig
    {
        /// <summary>
        /// The authentication mode to use.
        /// </summary>
        public DbAuthMode Mode { get; init; } = DbAuthMode.SharedConnection;

        /// <summary>
        /// Maps JWT claim names to database context keys.
        /// For <see cref="DbAuthMode.SessionContext"/>, each entry becomes a
        /// sp_set_session_context call: key = context key name, value = claim name to read.
        /// For <see cref="DbAuthMode.PerUser"/>, values are used as template placeholders.
        /// </summary>
        public IReadOnlyDictionary<string, string> ClaimMappings { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// The JWT claim whose value identifies the database user for impersonation.
        /// Required when <see cref="Mode"/> is <see cref="DbAuthMode.Impersonation"/>.
        /// </summary>
        public string? ImpersonationClaimKey { get; init; }

        /// <summary>
        /// Connection string template for per-user mode. Placeholders use the format
        /// {claim_name} and are replaced with the corresponding JWT claim values.
        /// Required when <see cref="Mode"/> is <see cref="DbAuthMode.PerUser"/>.
        /// </summary>
        public string? ConnectionStringTemplate { get; init; }

        /// <summary>
        /// Validates that the configuration is complete for the selected mode.
        /// Returns a list of validation error messages. An empty list means valid.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();

            switch (Mode)
            {
                case DbAuthMode.SharedConnection:
                    break;

                case DbAuthMode.Impersonation:
                    if (string.IsNullOrWhiteSpace(ImpersonationClaimKey))
                        errors.Add("ImpersonationClaimKey is required when Mode is Impersonation.");
                    break;

                case DbAuthMode.SessionContext:
                    if (ClaimMappings.Count == 0)
                        errors.Add("ClaimMappings must contain at least one entry when Mode is SessionContext.");
                    break;

                case DbAuthMode.PerUser:
                    if (string.IsNullOrWhiteSpace(ConnectionStringTemplate))
                        errors.Add("ConnectionStringTemplate is required when Mode is PerUser.");
                    if (ClaimMappings.Count == 0)
                        errors.Add("ClaimMappings must contain at least one entry when Mode is PerUser.");
                    break;

                default:
                    errors.Add($"Unknown DbAuthMode: {Mode}.");
                    break;
            }

            return errors;
        }
    }
}
