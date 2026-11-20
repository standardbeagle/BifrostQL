using System.Collections.Concurrent;
using BifrostQL.Core.Operations;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A host answers a constraint violation with its own code and sentence through an
/// <see cref="IDatabaseErrorMapper"/>, instead of reading first to repeat the constraint as a
/// check (which races and costs a statement). The mapper is handed the kind, the constraint's
/// name and the table; a violation it returns null for keeps BifrostQL's answer. It applies to a
/// generated write, a <c>_batch</c> and a server operation alike.
/// </summary>
public sealed class PostgresConstraintMappingTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE projects (
            id SERIAL PRIMARY KEY,
            name TEXT NOT NULL CONSTRAINT projects_name_unique UNIQUE,
            code TEXT CONSTRAINT projects_code_unique UNIQUE,
            budget INT CONSTRAINT projects_budget_positive CHECK (budget > 0));
        INSERT INTO projects (name, code) VALUES ('Alpha', 'A1');
        """;

    private readonly ConcurrentQueue<DatabaseConstraintViolation> _asked = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_constraint_mapping", Ddl, new[] { ":root { operations: addProject[writer] }" },
            services: s => s
                .AddSingleton(_asked)
                .AddBifrostDatabaseErrorMapper<ProjectNameMapper>(),
            bifrost: options => options.AddOperation<AddProject>());
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task MappedUniqueViolation_AnswersTheHostsCodeAndMessage()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { projects(insert: { name: "Alpha" }) }""");

        AssertMapped(reply);
        _asked.Should().ContainSingle().Which.Should().Be(
            new DatabaseConstraintViolation(DatabaseConstraintKind.Unique, "projects_name_unique", "public", "projects"));
    }

    [SkippableFact]
    public async Task MappedUniqueViolation_AnswersEveryLosingConcurrentInsert()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            _host!.PostAsync("""mutation { projects(insert: { name: "Raced" }) }""")));

        replies.Count(r => !r.RootElement.TryGetProperty("errors", out _)).Should().Be(1);
        foreach (var lost in replies.Where(r => r.RootElement.TryGetProperty("errors", out _)))
            AssertMapped(lost);
        (await _host!.CountAsync("SELECT count(*) FROM projects WHERE name = 'Raced'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task MappedViolation_InABatch_AnswersTheHostsCode()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync(
            """mutation { projects_batch(actions: [{ insert: { name: "Beta" } }, { insert: { name: "Alpha" } }]) }""");

        AssertMapped(reply);
        (await _host.CountAsync("SELECT count(*) FROM projects WHERE name = 'Beta'")).Should().Be(0);
    }

    [SkippableFact]
    public async Task UnmappedViolation_KeepsBifrostsAnswer()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var unique = PostgresGraphQlHost.FirstError(
            await _host!.PostAsync("""mutation { projects(insert: { name: "Gamma", code: "A1" }) }"""));
        unique.GetProperty("extensions").GetProperty("code").GetString().Should().Be(BifrostExecutionError.ConflictCode);
        unique.GetProperty("extensions").GetProperty("constraint").GetString().Should().Be("projects_code_unique");

        var check = PostgresGraphQlHost.FirstError(
            await _host.PostAsync("""mutation { projects(insert: { name: "Delta", budget: -1 }) }"""));
        check.GetProperty("extensions").GetProperty("code").GetString().Should().Be("DATABASE_ERROR");

        _asked.Should().Equal(
            new DatabaseConstraintViolation(DatabaseConstraintKind.Unique, "projects_code_unique", "public", "projects"),
            new DatabaseConstraintViolation(DatabaseConstraintKind.Check, "projects_budget_positive", "public", "projects"));
    }

    [SkippableFact]
    public async Task MappedViolation_InsideAnOperation_FailsTheOperation_WithTheHostsCode()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { addProject(name: "Alpha", then: "Epsilon") }""", "u-1", "writer");

        AssertMapped(reply);
        // The operation's transaction rolled back, so the write it made before the violation is gone.
        (await _host.CountAsync("SELECT count(*) FROM projects WHERE name = 'Epsilon'")).Should().Be(0);
    }

    private static void AssertMapped(System.Text.Json.JsonDocument reply)
    {
        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("message").GetString().Should().Be(ProjectNameMapper.Message);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(ProjectNameMapper.Code);
    }

    /// <summary>Answers the project-name unique index with the host's own code; nothing else.</summary>
    private sealed class ProjectNameMapper(ConcurrentQueue<DatabaseConstraintViolation> asked) : IDatabaseErrorMapper
    {
        public const string Code = "validation_failed";
        public const string Message = "name: a project with this name already exists.";

        public BifrostExecutionError? Map(DatabaseConstraintViolation violation)
        {
            asked.Enqueue(violation);
            return violation is { Kind: DatabaseConstraintKind.Unique, ConstraintName: "projects_name_unique" }
                ? new BifrostExecutionError(Message) { ErrorCode = Code }
                : null;
        }
    }

    /// <summary>Inserts <c>then</c> first, then <c>name</c>, in one operation.</summary>
    private sealed class AddProject : IBifrostOperation
    {
        public string Name => "addProject";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(name: String!, then: String!): Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await context.InsertAsync("projects", new Dictionary<string, object?> { ["name"] = context.GetArgument<string>("then") });
            return await context.InsertAsync("projects", new Dictionary<string, object?> { ["name"] = context.GetArgument<string>("name") });
        }
    }
}
