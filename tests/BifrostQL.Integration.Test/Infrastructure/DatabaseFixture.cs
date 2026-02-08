namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// xUnit class fixture that wraps an IIntegrationTestDatabase.
/// Handles creation/destruction lifecycle for shared test database instances.
/// </summary>
public sealed class DatabaseFixture<TDatabase> : IAsyncLifetime
    where TDatabase : IIntegrationTestDatabase, new()
{
    public TDatabase Database { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        Database = new TDatabase();
        await Database.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Database.DisposeAsync();
    }
}
