using System.Collections.Generic;
using System.Threading.Tasks;
using BifrostQL.Server;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Covers the GraphQL depth/complexity limits (SEC-MED-3): the configuration reader's
    /// secure defaults and non-positive handling, plus an end-to-end proof that the
    /// configured analyzer actually rejects an over-deep query on the shared executor.
    /// </summary>
    public class GraphQlComplexityLimitsTests
    {
        [Fact]
        public void Read_NullSection_ReturnsSecureDefaultDepthAndNoComplexity()
        {
            var (maxDepth, maxComplexity) = GraphQlComplexityLimits.Read(null);

            maxDepth.Should().Be(GraphQlComplexityLimits.DefaultMaxDepth);
            maxComplexity.Should().BeNull();
        }

        [Fact]
        public void Read_ConfiguredValues_AreHonored()
        {
            var section = Section(new Dictionary<string, string?>
            {
                ["limits:MaxQueryDepth"] = "7",
                ["limits:MaxQueryComplexity"] = "500",
            });

            var (maxDepth, maxComplexity) = GraphQlComplexityLimits.Read(section);

            maxDepth.Should().Be(7);
            maxComplexity.Should().Be(500);
        }

        [Fact]
        public void Read_NonPositiveDepth_FallsBackToDefault_NotDisabled()
        {
            var section = Section(new Dictionary<string, string?>
            {
                ["limits:MaxQueryDepth"] = "0",
                ["limits:MaxQueryComplexity"] = "-1",
            });

            var (maxDepth, maxComplexity) = GraphQlComplexityLimits.Read(section);

            maxDepth.Should().Be(GraphQlComplexityLimits.DefaultMaxDepth,
                "a zero/negative depth must not silently disable the guard");
            maxComplexity.Should().BeNull();
        }

        [Fact]
        public async Task ConfiguredAnalyzer_RejectsOverDeepQuery_ButAllowsShallowQuery()
        {
            var schema = Schema.For(
                "type Query { me: Person } type Person { name: String friend: Person }");

            var services = new ServiceCollection();
            services.AddGraphQL(b => b
                .AddSystemTextJson()
                .AddComplexityAnalyzer(c => GraphQlComplexityLimits.Apply(c, maxDepth: 3, maxComplexity: null)));
            await using var provider = services.BuildServiceProvider();
            var executer = provider.GetRequiredService<IDocumentExecuter>();

            var deep = await executer.ExecuteAsync(o =>
            {
                o.Schema = schema;
                o.Query = "{ me { friend { friend { friend { friend { name } } } } } }";
                o.RequestServices = provider;
            });

            deep.Errors.Should().NotBeNullOrEmpty(
                "a query nested deeper than the configured max depth must be rejected");

            var shallow = await executer.ExecuteAsync(o =>
            {
                o.Schema = schema;
                o.Query = "{ me { name } }";
                o.RequestServices = provider;
            });

            (shallow.Errors ?? new ExecutionErrors()).Should().BeEmpty(
                "a query within the depth limit must pass validation");
        }

        private static IConfigurationSection Section(Dictionary<string, string?> values)
            => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("limits");
    }
}
