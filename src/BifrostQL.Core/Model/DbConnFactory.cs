using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Supported database providers for BifrostQL.
    /// </summary>
    public enum BifrostDbProvider
    {
        SqlServer,
        PostgreSql,
        MySql,
        Sqlite
    }

    /// <summary>
    /// Factory interface that provides database-specific components for BifrostQL.
    /// Each database dialect (SQL Server, PostgreSQL, MySQL, SQLite) has its own
    /// implementation that wires together the connection, SQL dialect, schema reader,
    /// and type mapper for that database.
    /// </summary>
    public interface IDbConnFactory
    {
        /// <summary>
        /// Creates and returns a new, unopened database connection.
        /// The caller is responsible for opening and disposing the connection.
        /// </summary>
        /// <returns>A new <see cref="DbConnection"/> configured with the factory's connection string.</returns>
        DbConnection GetConnection();

        /// <summary>
        /// The SQL dialect for generating database-specific SQL syntax
        /// (identifier escaping, pagination, LIKE patterns).
        /// </summary>
        ISqlDialect Dialect { get; }

        /// <summary>
        /// The schema reader for loading table, column, and constraint metadata
        /// from the database's system catalog or information schema.
        /// </summary>
        ISchemaReader SchemaReader { get; }

        /// <summary>
        /// The type mapper for converting database-specific data types to GraphQL types.
        /// </summary>
        ITypeMapper TypeMapper { get; }
    }

    /// <summary>
    /// Default SQL Server connection factory in the Core package.
    /// For explicit dialect selection, prefer <see cref="BifrostQL.SqlServer.SqlServerDbConnFactory"/>
    /// from the BifrostQL.SqlServer package.
    /// </summary>
    public class DbConnFactory : IDbConnFactory
    {
        private readonly string _connectionString;

        /// <summary>
        /// Creates a new SQL Server connection factory.
        /// </summary>
        /// <param name="connectionString">SQL Server connection string.</param>
        public DbConnFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <inheritdoc />
        public ISqlDialect Dialect => SqlServerDialect.Instance;

        /// <inheritdoc />
        public ISchemaReader SchemaReader => new SqlServerSchemaReader();

        /// <inheritdoc />
        public ITypeMapper TypeMapper => SqlServerTypeMapper.Instance;

        /// <inheritdoc />
        public DbConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }

    /// <summary>
    /// Resolves the appropriate <see cref="IDbConnFactory"/> for a connection string,
    /// either by explicit provider selection or by auto-detecting the database type
    /// from connection string patterns.
    /// </summary>
    public static class DbConnFactoryResolver
    {
        private static readonly ConcurrentDictionary<BifrostDbProvider, Func<string, IDbConnFactory>> _registry = new();

        /// <summary>
        /// Registers a factory creator for a specific database provider.
        /// Dialect packages call this at startup to make themselves available for auto-detection.
        /// </summary>
        public static void Register(BifrostDbProvider provider, Func<string, IDbConnFactory> factoryCreator)
        {
            ArgumentNullException.ThrowIfNull(factoryCreator);
            _registry[provider] = factoryCreator;
        }

        /// <summary>
        /// Clears all registered factory creators. Intended for testing only.
        /// </summary>
        internal static void ClearRegistrations()
        {
            _registry.Clear();
        }

        /// <summary>
        /// Creates an <see cref="IDbConnFactory"/> for the given connection string.
        /// If <paramref name="provider"/> is specified, uses that provider directly.
        /// Otherwise, auto-detects the provider from connection string patterns.
        /// Falls back to SQL Server (via the built-in <see cref="DbConnFactory"/>) when
        /// a dialect package is not registered.
        /// </summary>
        public static IDbConnFactory Create(string connectionString, BifrostDbProvider? provider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            var resolvedProvider = provider ?? DetectProvider(connectionString);

            if (_registry.TryGetValue(resolvedProvider, out var creator))
                return creator(connectionString);

            if (resolvedProvider == BifrostDbProvider.SqlServer)
                return new DbConnFactory(connectionString);

            throw new InvalidOperationException(
                $"No factory registered for provider '{resolvedProvider}'. " +
                $"Add a reference to the appropriate BifrostQL dialect package " +
                $"(e.g., BifrostQL.SqlServer, BifrostQL.Ngsql, BifrostQL.MySql, BifrostQL.Sqlite).");
        }

        /// <summary>
        /// Detects the database provider from connection string patterns.
        /// </summary>
        /// <remarks>
        /// Detection rules:
        /// <list type="bullet">
        /// <item>"Data Source=*.db" or "Data Source=:memory:" or "Filename=" -> SQLite</item>
        /// <item>"Host=" or "Username=" (PostgreSQL-style keys) -> PostgreSQL</item>
        /// <item>"Uid=" or "Port=3306" or "SslMode=" with MySQL patterns -> MySQL</item>
        /// <item>Everything else (including "Server=", "Data Source=", "Initial Catalog=") -> SQL Server</item>
        /// </list>
        /// </remarks>
        public static BifrostDbProvider DetectProvider(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            if (IsSqlite(connectionString))
                return BifrostDbProvider.Sqlite;

            if (IsPostgreSql(connectionString))
                return BifrostDbProvider.PostgreSql;

            if (IsMySql(connectionString))
                return BifrostDbProvider.MySql;

            return BifrostDbProvider.SqlServer;
        }

        /// <summary>
        /// Parses a provider name string into a <see cref="BifrostDbProvider"/>.
        /// Accepts case-insensitive values: "sqlserver", "postgresql", "postgres", "mysql", "sqlite".
        /// </summary>
        public static BifrostDbProvider ParseProviderName(string providerName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

            return providerName.Trim().ToLowerInvariant() switch
            {
                "sqlserver" or "mssql" => BifrostDbProvider.SqlServer,
                "postgresql" or "postgres" or "npgsql" or "pgsql" => BifrostDbProvider.PostgreSql,
                "mysql" or "mariadb" => BifrostDbProvider.MySql,
                "sqlite" => BifrostDbProvider.Sqlite,
                _ => throw new ArgumentException(
                    $"Unknown provider '{providerName}'. " +
                    $"Supported values: sqlserver, postgresql, postgres, mysql, mariadb, sqlite.",
                    nameof(providerName))
            };
        }

        private static bool IsSqlite(string connectionString)
        {
            var parts = ParseConnectionStringParts(connectionString);

            // SQLite uses "Data Source=file.db" or "Data Source=:memory:" or "Filename="
            if (parts.TryGetValue("filename", out _))
                return true;

            if (parts.TryGetValue("data source", out var dataSource))
            {
                if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (dataSource.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                    dataSource.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                    dataSource.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // "Mode=Memory" is a SQLite-specific key
            if (parts.ContainsKey("mode"))
                return true;

            return false;
        }

        private static bool IsPostgreSql(string connectionString)
        {
            var parts = ParseConnectionStringParts(connectionString);

            // PostgreSQL uses "Host=" and "Username=" (not "User Id=" like SQL Server)
            if (parts.ContainsKey("host") && !parts.ContainsKey("data source"))
                return true;

            // "Username=" is PostgreSQL-specific (SQL Server uses "User Id=")
            if (parts.ContainsKey("username"))
                return true;

            return false;
        }

        private static bool IsMySql(string connectionString)
        {
            var parts = ParseConnectionStringParts(connectionString);

            // MySQL uses "Uid=" (not "User Id=")
            if (parts.ContainsKey("uid"))
                return true;

            // MySQL uses "Pwd=" (not "Password=" typically, though both work)
            if (parts.ContainsKey("pwd") && !parts.ContainsKey("user id"))
                return true;

            // "SslMode=" combined with "Server=" but no "Initial Catalog=" or "Database=" with
            // SQL Server-style Data Source is likely MySQL
            if (parts.ContainsKey("sslmode") && parts.ContainsKey("server") && !parts.ContainsKey("initial catalog"))
            {
                // Could be MySQL; check for port 3306
                if (parts.TryGetValue("port", out var port) && port == "3306")
                    return true;
                // Server with SslMode but no Host (which would be PG) suggests MySQL
                if (!parts.ContainsKey("host"))
                    return true;
            }

            return false;
        }

        internal static Dictionary<string, string> ParseConnectionStringParts(string connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = part.IndexOf('=');
                if (eqIndex <= 0) continue;
                var key = part[..eqIndex].Trim();
                var value = part[(eqIndex + 1)..].Trim();
                result[key] = value;
            }
            return result;
        }
    }
}
