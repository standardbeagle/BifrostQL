using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// The gRPC method set is generated from the model when the routes are mapped. Mapping used to
    /// block the calling thread on the async model load (sync-over-async at startup). These facts
    /// drive <c>MapBifrostGrpcAsync</c> against a COLD model load, one that has not completed, and
    /// prove the call yields instead of blocking, then maps every RPC once the load completes.
    /// </summary>
    public class GrpcColdModelLoadTests
    {
        private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Mapping_awaits_a_cold_model_load_without_blocking_the_caller()
        {
            var coldLoad = new TaskCompletionSource<IDbModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(coldLoad.Task);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(executor);
            builder.Services.AddSingleton(Substitute.For<IMutationIntentExecutor>());
            builder.Services.AddBifrostGrpc(_ => { });
            await using var app = builder.Build();
            app.UseRouting();

            // Invoke on a pool thread so a blocking implementation hangs that thread, not the test.
            var invocation = Task.Factory.StartNew(
                () => app.MapBifrostGrpcAsync(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                var returned = await Task.WhenAny(invocation, Task.Delay(ReturnBudget)) == invocation;
                returned.Should().BeTrue(
                    "MapBifrostGrpcAsync must return a pending task while the model load is cold, not block on it");
                var mapping = await invocation;
                mapping.IsCompleted.Should().BeFalse("the model has not loaded, so mapping cannot have finished");
                RpcRoutes(app).Should().BeEmpty("no RPC can be mapped before the model loads");

                coldLoad.SetResult(Model());
                await mapping;
            }
            finally
            {
                // Releases a blocked mutant's thread so the run ends.
                coldLoad.TrySetResult(Model());
            }

            RpcRoutes(app).Should().Contain("/bifrostql.BifrostQuery/GetWidgets")
                .And.Contain("/bifrostql.BifrostQuery/ListWidgets")
                .And.Contain("/bifrostql.BifrostQuery/StreamWidgets");
        }

        [Fact]
        public async Task A_faulted_model_load_is_retried_by_the_next_mapping()
        {
            // The first load faults (the database is down at startup); the second succeeds. The
            // contract build must not keep the faulted task: a later MapBifrostGrpcAsync retries.
            // Built first: Model() configures substitutes, which would steal Returns' last call.
            var model = Model();
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(
                Task.FromException<IDbModel>(new InvalidOperationException("model load failed")),
                Task.FromResult(model));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(executor);
            builder.Services.AddSingleton(Substitute.For<IMutationIntentExecutor>());
            builder.Services.AddBifrostGrpc(_ => { });
            await using var app = builder.Build();
            app.UseRouting();

            var first = () => app.MapBifrostGrpcAsync();
            await first.Should().ThrowAsync<InvalidOperationException>().WithMessage("model load failed");
            RpcRoutes(app).Should().BeEmpty("a failed build maps nothing");

            await app.MapBifrostGrpcAsync();

            RpcRoutes(app).Should().Contain("/bifrostql.BifrostQuery/GetWidgets");
            await executor.Received(2).GetModelAsync(Arg.Any<string?>());
        }

        private static IEnumerable<string?> RpcRoutes(WebApplication app) =>
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .Where(route => route is not null && route.StartsWith("/bifrostql.BifrostQuery/", StringComparison.Ordinal));

        private static IDbModel Model()
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns("Widgets");
            table.DbName.Returns("Widgets");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", IsPrimaryKey = true },
            });
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns((string?)null);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns((string?)null);

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { table });
            return model;
        }
    }
}
