using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the
    /// reference <see cref="EchoProtocolAdapter"/>. This derivation is the template
    /// for real adapters (RESP, MQTT, pgwire, …): register the adapter, translate
    /// <see cref="ConformanceReadRequest"/> into the wire format, and let server
    /// errors propagate — the base class supplies every fact.
    /// </summary>
    public sealed class EchoProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        protected override void RegisterAdapter(BifrostMultiDbOptions options)
            => options.AddProtocolAdapter<EchoProtocolAdapter>();

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            var adapter = Host.Services.GetRequiredService<EchoProtocolAdapter>();
            return await adapter.ExecuteAsync(new EchoRequest
            {
                Table = request.Table,
                Columns = request.Columns,
                Filter = request.Filter,
                Principal = request.Principal,
                Endpoint = request.Endpoint,
            });
        }

        // The echo adapter exposes writes (MutateAsync), so it must prove the
        // mutation facts; a read-only adapter would leave these members alone.
        protected override bool AdapterSupportsMutations => true;

        protected override async Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
        {
            var adapter = Host.Services.GetRequiredService<EchoProtocolAdapter>();
            return await adapter.MutateAsync(new EchoMutationRequest
            {
                Table = request.Table,
                Action = request.Action switch
                {
                    ConformanceMutationAction.Insert => MutationIntentAction.Insert,
                    ConformanceMutationAction.Update => MutationIntentAction.Update,
                    ConformanceMutationAction.Delete => MutationIntentAction.Delete,
                    _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown conformance mutation action."),
                },
                Data = request.Data,
                PrimaryKey = request.PrimaryKey,
                Principal = request.Principal,
                Endpoint = request.Endpoint,
            });
        }
    }
}
