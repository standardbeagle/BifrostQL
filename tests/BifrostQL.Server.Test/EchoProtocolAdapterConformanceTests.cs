using BifrostQL.AdapterConformance;
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
    }
}
