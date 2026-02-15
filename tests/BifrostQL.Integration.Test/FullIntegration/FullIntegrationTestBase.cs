using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using System.Data.Common;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Base class for full integration tests that load schema dynamically,
/// build GraphQL schema, and execute queries against real databases.
/// </summary>
public abstract class FullIntegrationTestBase
{
    protected IDbModel Model { get; private set; } = null!;
    protected ISchema GraphQLSchema { get; private set; } = null!;
    protected DbConnection Connection { get; private set; } = null!;

    protected async Task InitializeAsync(IDbConnFactory connFactory, Func<DbConnection, Task> createSchema, Func<DbConnection, Task> seedData)
    {
        Connection = connFactory.GetConnection();
        await Connection.OpenAsync();

        // Create schema
        await createSchema(Connection);

        // Load schema using DbModelLoader
        var metadataLoader = new MetadataLoader();
        var loader = new DbModelLoader(connFactory, metadataLoader);
        Model = await loader.LoadAsync();

        // Build GraphQL schema from loaded model
        var builder = new DbSchemaBuilder(Model);
        GraphQLSchema = builder.Build();

        // Seed test data
        await seedData(Connection);
    }

    protected async Task<ExecutionResult> ExecuteQueryAsync(string query, Dictionary<string, object?>? variables = null)
    {
        var result = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = GraphQLSchema;
            options.Query = query;
            if (variables != null)
                options.Variables = new GraphQL.Inputs(variables);
        });

        return result;
    }

    protected async Task<T?> ExecuteQueryAsync<T>(string query, string resultPath, Dictionary<string, object?>? variables = null)
    {
        var result = await ExecuteQueryAsync(query, variables);

        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();

        var data = result.Data as Dictionary<string, object?>;
        data.Should().ContainKey(resultPath);

        return (T?)data![resultPath];
    }

    protected async Task CleanupAsync()
    {
        if (Connection != null)
        {
            await Connection.DisposeAsync();
        }
    }
}
