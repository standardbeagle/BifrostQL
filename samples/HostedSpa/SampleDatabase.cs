using Microsoft.Data.Sqlite;

namespace BifrostQL.Samples.HostedSpa;

/// <summary>
/// Creates and seeds the small SQLite database used by the HostedSpa sample so the
/// sample runs without any external database setup.
/// </summary>
internal static class SampleDatabase
{
    /// <summary>
    /// Resolves the SQLite database file path from the configured connection string.
    /// Falls back to <c>hostedspa-sample.db</c> under <paramref name="contentRootPath"/>
    /// when no connection string or <c>Data Source</c> is configured. Relative
    /// <c>Data Source</c> values are resolved against the content root.
    /// </summary>
    /// <param name="connectionString">The configured <c>bifrost</c> connection string, if any.</param>
    /// <param name="contentRootPath">The host content root used to resolve relative paths.</param>
    /// <returns>An absolute path to the SQLite database file.</returns>
    public static string ResolveDbPath(string? connectionString, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var dataSource = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
            dataSource = "hostedspa-sample.db";

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(contentRootPath, dataSource);
    }

    /// <summary>
    /// Creates the sample database file with a single seeded <c>widgets</c> table
    /// if it does not already exist. Existing files are left untouched.
    /// </summary>
    /// <param name="dbPath">Absolute path to the SQLite database file.</param>
    public static void EnsureCreated(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        if (File.Exists(dbPath))
            return;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                color TEXT NOT NULL
            );
            INSERT INTO widgets (name, color) VALUES ('Sprocket', 'red');
            INSERT INTO widgets (name, color) VALUES ('Gear', 'blue');
            INSERT INTO widgets (name, color) VALUES ('Cog', 'green');
            """;
        command.ExecuteNonQuery();
    }
}
