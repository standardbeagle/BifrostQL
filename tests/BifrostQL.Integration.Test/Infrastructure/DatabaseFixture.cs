namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// xUnit class fixture that wraps an IIntegrationTestDatabase.
/// Handles creation/destruction lifecycle for shared test database instances.
/// When the database is unavailable (e.g., missing env var), tests are skipped rather than failed.
/// </summary>
public sealed class DatabaseFixture<TDatabase> : IAsyncLifetime
    where TDatabase : IIntegrationTestDatabase, new()
{
    public TDatabase Database { get; private set; } = default!;
    public bool IsAvailable { get; private set; }
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        Database = new TDatabase();
        try
        {
            await Database.InitializeAsync();
            IsAvailable = true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("environment variable not set"))
        {
            IsAvailable = false;
            SkipReason = ex.Message;
        }
    }

    public void EnsureAvailable()
    {
        Xunit.Skip.IfNot(IsAvailable, SkipReason ?? $"{typeof(TDatabase).Name} is not available");
    }

    public async Task DisposeAsync()
    {
        if (IsAvailable)
            await Database.DisposeAsync();
    }
}
