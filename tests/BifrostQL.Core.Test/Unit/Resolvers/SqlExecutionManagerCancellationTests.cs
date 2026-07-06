using System.Data.Common;
using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.SqlServer;
using FluentAssertions;
using GraphQL.Types;
using NSubstitute;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Proves <see cref="SqlExecutionManager"/> threads the request's
/// <see cref="IBifrostFieldContext.CancellationToken"/> into the ADO.NET calls of
/// <c>LoadDataParameterizedAsync</c>. Before the fix every DB call was token-less,
/// so a client abort left the SQL running and the pooled connection occupied.
/// </summary>
public sealed class SqlExecutionManagerCancellationTests
{
    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Total", "decimal"))
            .Build();

    private static GqlObjectQuery BuildQuery(IDbModel model) => new()
    {
        DbTable = model.GetTableFromDbName("Orders"),
        TableName = "Orders",
        SchemaName = "dbo",
        GraphQlName = "Orders",
        ScalarColumns = { new GqlObjectColumn("Id") },
    };

    private static Task InvokeLoadDataAsync(
        SqlExecutionManager manager, GqlObjectQuery query, IDbConnFactory connFactory, CancellationToken token)
    {
        var method = typeof(SqlExecutionManager).GetMethod(
            "LoadDataParameterizedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(manager, new object?[] { query, connFactory, token })!;
    }

    private static (IDbConnFactory factory, DbConnection conn) MakeConnFactory()
    {
        var conn = Substitute.For<DbConnection>();
        var factory = Substitute.For<IDbConnFactory>();
        factory.GetConnection().Returns(conn);
        factory.Dialect.Returns(SqlServerDialect.Instance);
        return (factory, conn);
    }

    [Fact]
    public async Task LoadData_PassesRequestTokenToConnectionOpen()
    {
        // Arrange — capture the token OpenAsync receives, then stop the pipeline.
        var model = BuildModel();
        var manager = new SqlExecutionManager(model, Substitute.For<ISchema>());
        var (factory, conn) = MakeConnFactory();

        using var cts = new CancellationTokenSource();
        var captured = CancellationToken.None;
        conn.OpenAsync(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            captured = ci.Arg<CancellationToken>();
            throw new InvalidOperationException("stop here");
        });

        // Act
        var act = () => InvokeLoadDataAsync(manager, BuildQuery(model), factory, cts.Token);

        // Assert — the DB call saw the request token, not CancellationToken.None.
        await act.Should().ThrowAsync<BifrostExecutionError>();
        captured.Should().Be(cts.Token);
    }

    [Fact]
    public async Task LoadData_CancelledToken_SurfacesOperationCanceledUnwrapped()
    {
        // Arrange — a pre-cancelled token makes OpenAsync return a canceled task,
        // as real providers do when the token is already signalled.
        var model = BuildModel();
        var manager = new SqlExecutionManager(model, Substitute.For<ISchema>());
        var (factory, conn) = MakeConnFactory();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        conn.OpenAsync(Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromCanceled(ci.Arg<CancellationToken>()));

        // Act
        var act = () => InvokeLoadDataAsync(manager, BuildQuery(model), factory, cts.Token);

        // Assert — cancellation propagates as OperationCanceledException, not
        // wrapped into a BifrostExecutionError that would mask the abort.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
