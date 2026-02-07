using System;
using System.Collections.Generic;
using System.Linq;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Configures a single database to be exposed as a top-level GraphQL field.
    /// Each database field has its own connection string, alias, and access control.
    /// </summary>
    public sealed class DatabaseFieldConfig
    {
        /// <summary>
        /// The GraphQL field name used to access this database (e.g., "userDb", "orderDb").
        /// Must be a valid GraphQL identifier: lowercase start, alphanumeric and underscores only.
        /// </summary>
        public string Alias { get; set; } = null!;

        /// <summary>
        /// The database connection string for this field.
        /// </summary>
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// Roles that are allowed to query this database. Empty means all roles are allowed.
        /// </summary>
        public IReadOnlyList<string> AllowedRoles { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Whether this database is the default (used when no field qualifier is specified in joins).
        /// Only one database can be marked as default.
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>
        /// Metadata rules for this database's tables (e.g., tenant filters, soft-delete).
        /// </summary>
        public IReadOnlyCollection<string> Metadata { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Defines which databases can participate in cross-database joins and which
    /// keys are used to match rows between them.
    /// </summary>
    public sealed class CrossDbJoinConfig
    {
        /// <summary>
        /// The alias of the source database (where the join originates).
        /// </summary>
        public string SourceAlias { get; set; } = null!;

        /// <summary>
        /// The alias of the target database (where the join resolves to).
        /// </summary>
        public string TargetAlias { get; set; } = null!;

        /// <summary>
        /// The table name in the source database that participates in the join.
        /// </summary>
        public string SourceTable { get; set; } = null!;

        /// <summary>
        /// The column name in the source table used as the join key.
        /// </summary>
        public string SourceColumn { get; set; } = null!;

        /// <summary>
        /// The table name in the target database that is joined to.
        /// </summary>
        public string TargetTable { get; set; } = null!;

        /// <summary>
        /// The column name in the target table used as the join key.
        /// </summary>
        public string TargetColumn { get; set; } = null!;
    }

    /// <summary>
    /// Configuration for exposing multiple databases as top-level fields within a single
    /// GraphQL schema. Enables queries like:
    /// <code>
    /// query {
    ///   userDb { users { id name } }
    ///   orderDb { orders { id total } }
    /// }
    /// </code>
    /// </summary>
    public sealed class MultiDbConfig
    {
        private readonly List<DatabaseFieldConfig> _databases = new();
        private readonly List<CrossDbJoinConfig> _crossJoins = new();

        /// <summary>
        /// The configured database fields.
        /// </summary>
        public IReadOnlyList<DatabaseFieldConfig> Databases => _databases;

        /// <summary>
        /// The configured cross-database join definitions.
        /// </summary>
        public IReadOnlyList<CrossDbJoinConfig> CrossJoins => _crossJoins;

        /// <summary>
        /// Adds a database field configuration. Validates that the alias is unique
        /// and that the alias is a valid GraphQL identifier.
        /// </summary>
        public MultiDbConfig AddDatabase(Action<DatabaseFieldConfig> configure)
        {
            var config = new DatabaseFieldConfig();
            configure(config);
            Validate(config);
            _databases.Add(config);
            return this;
        }

        /// <summary>
        /// Adds a cross-database join definition. Both source and target aliases
        /// must reference previously added databases.
        /// </summary>
        public MultiDbConfig AddCrossJoin(Action<CrossDbJoinConfig> configure)
        {
            var join = new CrossDbJoinConfig();
            configure(join);
            ValidateCrossJoin(join);
            _crossJoins.Add(join);
            return this;
        }

        /// <summary>
        /// Gets the database configuration by alias. Throws if not found.
        /// </summary>
        public DatabaseFieldConfig GetDatabase(string alias)
        {
            return _databases.FirstOrDefault(d =>
                       string.Equals(d.Alias, alias, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No database configured with alias '{alias}'.");
        }

        /// <summary>
        /// Gets the default database, or null if none is marked as default.
        /// </summary>
        public DatabaseFieldConfig? GetDefaultDatabase()
        {
            return _databases.FirstOrDefault(d => d.IsDefault);
        }

        /// <summary>
        /// Checks whether a user with the given roles can access the specified database.
        /// Returns true if the database has no role restrictions or if any of the user's
        /// roles match the allowed roles.
        /// </summary>
        public bool IsAccessAllowed(string alias, IReadOnlyCollection<string> userRoles)
        {
            var db = GetDatabase(alias);
            if (db.AllowedRoles.Count == 0)
                return true;
            return userRoles.Any(role =>
                db.AllowedRoles.Any(allowed =>
                    string.Equals(role, allowed, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Gets all cross-database join definitions where the specified alias is the source.
        /// </summary>
        public IReadOnlyList<CrossDbJoinConfig> GetCrossJoinsFrom(string sourceAlias)
        {
            return _crossJoins
                .Where(j => string.Equals(j.SourceAlias, sourceAlias, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void Validate(DatabaseFieldConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Alias))
                throw new ArgumentException("Alias is required for each database field.");

            if (!IsValidGraphQlIdentifier(config.Alias))
                throw new ArgumentException(
                    $"Alias '{config.Alias}' is not a valid GraphQL identifier. " +
                    "It must start with a letter or underscore and contain only letters, digits, and underscores.");

            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                throw new ArgumentException($"ConnectionString is required for database '{config.Alias}'.");

            if (_databases.Any(d => string.Equals(d.Alias, config.Alias, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A database with alias '{config.Alias}' is already configured.");

            if (config.IsDefault && _databases.Any(d => d.IsDefault))
            {
                var existing = _databases.First(d => d.IsDefault);
                throw new ArgumentException(
                    $"Only one database can be the default. '{existing.Alias}' is already marked as default.");
            }
        }

        private void ValidateCrossJoin(CrossDbJoinConfig join)
        {
            if (string.IsNullOrWhiteSpace(join.SourceAlias))
                throw new ArgumentException("SourceAlias is required for cross-database joins.");
            if (string.IsNullOrWhiteSpace(join.TargetAlias))
                throw new ArgumentException("TargetAlias is required for cross-database joins.");
            if (string.IsNullOrWhiteSpace(join.SourceTable))
                throw new ArgumentException("SourceTable is required for cross-database joins.");
            if (string.IsNullOrWhiteSpace(join.SourceColumn))
                throw new ArgumentException("SourceColumn is required for cross-database joins.");
            if (string.IsNullOrWhiteSpace(join.TargetTable))
                throw new ArgumentException("TargetTable is required for cross-database joins.");
            if (string.IsNullOrWhiteSpace(join.TargetColumn))
                throw new ArgumentException("TargetColumn is required for cross-database joins.");

            if (!_databases.Any(d => string.Equals(d.Alias, join.SourceAlias, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Source database '{join.SourceAlias}' is not configured.");
            if (!_databases.Any(d => string.Equals(d.Alias, join.TargetAlias, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Target database '{join.TargetAlias}' is not configured.");

            if (string.Equals(join.SourceAlias, join.TargetAlias, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Cross-database joins must reference two different databases.");
        }

        private static bool IsValidGraphQlIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_');
        }
    }
}
