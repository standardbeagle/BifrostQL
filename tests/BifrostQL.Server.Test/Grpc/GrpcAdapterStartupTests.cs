using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 1: the gRPC front door is opt-in (only present when <c>AddBifrostGrpc</c> is called),
    /// validates its port/TLS/stream configuration, and ABORTS STARTUP on a bad configuration or a
    /// descriptor-generation failure (fail-fast) rather than coming up half-configured or silently
    /// no-op. These exercise the startup guard directly.
    /// </summary>
    public class GrpcAdapterStartupTests
    {
        private static GrpcContractProvider ProviderFor(IDbModel model)
        {
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync("/graphql").Returns(Task.FromResult(model));
            return new GrpcContractProvider(executor, new GrpcWireOptions { Endpoint = "/graphql" });
        }

        private static GrpcContractProvider AnyProvider() =>
            ProviderFor(Model(Table("Widgets", Col("id", "int", pk: true))));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(70000)]
        public async Task Start_with_out_of_range_port_aborts(int port)
        {
            var adapter = new GrpcWireAdapter(new GrpcWireOptions { Port = port }, AnyProvider());

            var act = () => adapter.StartAsync(default);

            await act.Should().ThrowAsync<GrpcConfigurationException>().WithMessage("*port*");
        }

        [Fact]
        public async Task Start_with_non_positive_stream_bound_aborts()
        {
            var adapter = new GrpcWireAdapter(new GrpcWireOptions { MaxStreamRows = 0 }, AnyProvider());

            var act = () => adapter.StartAsync(default);

            await act.Should().ThrowAsync<GrpcConfigurationException>().WithMessage("*MaxStreamRows*");
        }

        [Fact]
        public async Task Start_requiring_tls_without_a_certificate_aborts()
        {
            var adapter = new GrpcWireAdapter(
                new GrpcWireOptions { RequireTls = true, TlsCertificatePath = null }, AnyProvider());

            var act = () => adapter.StartAsync(default);

            await act.Should().ThrowAsync<GrpcConfigurationException>().WithMessage("*TLS*");
        }

        [Fact]
        public async Task Start_requiring_tls_with_a_missing_certificate_file_aborts()
        {
            var adapter = new GrpcWireAdapter(
                new GrpcWireOptions { RequireTls = true, TlsCertificatePath = "/no/such/cert.pfx" }, AnyProvider());

            var act = () => adapter.StartAsync(default);

            await act.Should().ThrowAsync<GrpcConfigurationException>().WithMessage("*certificate*");
        }

        [Fact]
        public async Task Start_with_a_table_missing_a_primary_key_aborts_with_a_precise_diagnostic()
        {
            // Descriptor generation fails fast at startup: a Get RPC cannot identify a row with no PK.
            var provider = ProviderFor(Model(Table("Logs", Col("message", "varchar"))));
            var adapter = new GrpcWireAdapter(new GrpcWireOptions(), provider);

            var act = () => adapter.StartAsync(default);

            (await act.Should().ThrowAsync<GrpcSchemaException>())
                .WithMessage("*Logs*").WithMessage("*primary key*");
        }

        [Fact]
        public void Contract_provider_pins_field_numbers_across_full_and_filtered_projections()
        {
            // The full dispatch contract and an identity-filtered reflection projection must agree on
            // every field number, or a reflection-driven client mis-decodes the dispatch wire.
            var users = Table("Users",
                Col("aaa", "varchar"),           // sorts before the PK — the drift trap
                Col("id", "int", pk: true),
                Col("name", "varchar"));
            var provider = ProviderFor(Model(users));

            var full = provider.FullContract.Messages.Single(m => m.Name == "UsersRow");
            var filtered = provider.Generate(Admin()).Contract.Messages.Single(m => m.Name == "UsersRow");

            foreach (var name in new[] { "id", "name", "aaa" })
                filtered.Fields.Single(f => f.Name == name).Number
                    .Should().Be(full.Fields.Single(f => f.Name == name).Number,
                        $"field '{name}' must keep its number in both projections");
        }

        [Fact]
        public async Task Start_with_writes_enabled_logs_a_startup_warning()
        {
            // Enabling the write surface is a posture change worth surfacing (criterion 1).
            var logger = new CapturingLogger<GrpcWireAdapter>();
            var adapter = new GrpcWireAdapter(
                new GrpcWireOptions { EnableWrites = true }, AnyProvider(), logger);

            await adapter.StartAsync(default);

            logger.Warnings.Should().Contain(w => w.Contains("WRITES ARE ENABLED"));
        }

        [Fact]
        public async Task Start_with_writes_disabled_logs_no_write_warning()
        {
            var logger = new CapturingLogger<GrpcWireAdapter>();
            var adapter = new GrpcWireAdapter(new GrpcWireOptions(), AnyProvider(), logger);

            await adapter.StartAsync(default);

            logger.Warnings.Should().NotContain(w => w.Contains("WRITES ARE ENABLED"));
        }

        private static IDictionary<string, object?> Admin() =>
            new Dictionary<string, object?> { ["user_id"] = "u", ["roles"] = new[] { "admin" } };

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Warnings { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    Warnings.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        // ---- fixtures (mirror the schema-generator test helpers) ----

        private static IDbModel Model(params IDbTable[] tables)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);
            return model;
        }

        private static IDbTable Table(string name, params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns(name);
            table.DbName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns((string?)null);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns((string?)null);
            return table;
        }

        private static ColumnDto Col(string name, string dataType, bool pk = false, bool nullable = false) => new()
        {
            ColumnName = name,
            GraphQlName = name,
            DataType = dataType,
            IsPrimaryKey = pk,
            IsNullable = nullable,
        };
    }
}
