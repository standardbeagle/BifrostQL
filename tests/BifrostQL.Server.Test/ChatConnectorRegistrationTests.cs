using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Connector slice 3 registration wiring: the built-in
    /// <see cref="ExploreChatConnector"/> ships with the default chat-connector
    /// registration (mirroring the built-in transformers), so a host gets the
    /// explore tools with zero extra code the moment any <c>chat-connector:
    /// explore</c> binding exists — and never gets it twice.
    /// </summary>
    public class ChatConnectorRegistrationTests
    {
        private static ServiceProvider Build(params Type[] connectorTypes)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IQueryIntentExecutor>());
            BifrostServiceRegistrar.RegisterChatConnectorServices(services, connectorTypes);
            return services.BuildServiceProvider();
        }

        [Fact]
        public void RegisterChatConnectorServices_WiresTheExploreConnectorByDefault()
        {
            using var provider = Build();

            var registry = provider.GetRequiredService<ChatConnectorRegistry>();

            registry.Connectors.Should().ContainSingle()
                .Which.Should().BeOfType<ExploreChatConnector>()
                .Which.Priority.Should().Be(100);
        }

        [Fact]
        public void RegisterChatConnectorServices_HostAddingTheExploreConnectorAgain_DoesNotDuplicateIt()
        {
            // A duplicate registration would define every explore_* tool twice and
            // fail the registry's collision gate on the first chat request.
            using var provider = Build(typeof(ExploreChatConnector));

            provider.GetRequiredService<ChatConnectorRegistry>().Connectors
                .Should().ContainSingle().Which.Should().BeOfType<ExploreChatConnector>();
        }

        [Fact]
        public void RegisterChatConnectorServices_HostConnectors_RegisterAlongsideTheBuiltIn()
        {
            using var provider = Build(typeof(FakeHostConnector));

            provider.GetRequiredService<ChatConnectorRegistry>().Connectors
                .Select(c => c.GetType())
                .Should().BeEquivalentTo(new[] { typeof(ExploreChatConnector), typeof(FakeHostConnector) });
        }

        private sealed class FakeHostConnector : IChatConnector
        {
            public int Priority => 200;

            public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(
                BifrostQL.Core.Model.IDbModel model, ChatConnectorBinding binding)
                => Array.Empty<ChatToolDefinition>();

            public Task<ChatToolResult> ExecuteAsync(
                string toolName, string inputJson, IDictionary<string, object?> authContext,
                CancellationToken cancellationToken)
                => Task.FromResult(new ChatToolResult { TextPayload = "{}" });
        }
    }
}
