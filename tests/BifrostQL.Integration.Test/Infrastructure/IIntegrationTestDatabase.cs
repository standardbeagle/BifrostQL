using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// Abstraction for a test database that provides a connection factory and model.
/// Implementations handle database creation, schema setup, seeding, and cleanup.
/// </summary>
public interface IIntegrationTestDatabase : IAsyncDisposable
{
    IDbConnFactory ConnFactory { get; }
    ISqlDialect Dialect { get; }
    IDbModel DbModel { get; }
    string ProviderName { get; }
    ValueTask InitializeAsync();
}
