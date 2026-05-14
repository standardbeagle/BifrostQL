using Microsoft.Data.Sqlite;

namespace BifrostQL.Samples.HostedSpa;

/// <summary>
/// Creates and seeds the small SQLite database used by the HostedSpa sample so the
/// sample runs without any external database setup.
/// </summary>
internal static class SampleDatabase
{
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
