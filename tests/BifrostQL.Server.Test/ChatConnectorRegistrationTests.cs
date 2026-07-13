using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Connector slice 3/4 registration wiring: the built-in
    /// <see cref="ExploreChatConnector"/> and <see cref="MediaChatConnector"/> ship
    /// with the default chat-connector registration (mirroring the built-in
    /// transformers), so a host gets the generated tools with zero extra code the
    /// moment any <c>chat-connector</c> binding exists — and never gets one twice.
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
        public void RegisterChatConnectorServices_WiresTheBuiltInConnectorsByDefault_InPriorityOrder()
        {
            using var provider = Build();

            var registry = provider.GetRequiredService<ChatConnectorRegistry>();

            registry.Connectors.Should().HaveCount(2);
            registry.Connectors[0].Should().BeOfType<ExploreChatConnector>()
                .Which.Priority.Should().Be(100);
            registry.Connectors[1].Should().BeOfType<MediaChatConnector>()
                .Which.Priority.Should().Be(110);
        }

        [Fact]
        public void RegisterChatConnectorServices_HostAddingABuiltInConnectorAgain_DoesNotDuplicateIt()
        {
            // A duplicate registration would define every generated tool twice and
            // fail the registry's collision gate on the first chat request.
            using var provider = Build(typeof(ExploreChatConnector), typeof(MediaChatConnector));

            provider.GetRequiredService<ChatConnectorRegistry>().Connectors
                .Select(c => c.GetType())
                .Should().BeEquivalentTo(new[] { typeof(ExploreChatConnector), typeof(MediaChatConnector) });
        }

        [Fact]
        public void RegisterChatConnectorServices_HostConnectors_RegisterAlongsideTheBuiltIns()
        {
            using var provider = Build(typeof(FakeHostConnector));

            provider.GetRequiredService<ChatConnectorRegistry>().Connectors
                .Select(c => c.GetType())
                .Should().BeEquivalentTo(new[]
                {
                    typeof(ExploreChatConnector), typeof(MediaChatConnector), typeof(FakeHostConnector),
                });
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
