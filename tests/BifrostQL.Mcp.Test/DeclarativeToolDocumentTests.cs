using System.Text;
using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    public sealed class DeclarativeToolDocumentTests
    {
        private const string ValidDocument = """
            {
              "version": 1,
              "tools": [{
                "name": "get_customer_context",
                "description": "Return a customer and related order summary.",
                "params": {
                  "customerId": { "type": "id", "table": "dbo.customers" },
                  "detail": { "type": "enum", "values": ["summary", "full"], "default": "summary" }
                },
                "root": { "table": "dbo.customers", "byId": "customerId", "fields": ["id", "name"] },
                "include": [{
                  "relation": "orders",
                  "as": "totals",
                  "filter": { "status": { "_eq": "open" } },
                  "fields": ["id", "total"],
                  "sort": "-created_at",
                  "limit": 10,
                  "aggregate": { "count": true, "sum": "total" },
                  "detailGate": "full"
                }],
                "policy": { "allowedRoles": ["support"] }
              }]
            }
            """;

        [Fact]
        public async Task Loader_DeserializesVersionedTypedShape()
        {
            var document = await LoadAsync(ValidDocument);

            document.Version.Should().Be(1);
            var tool = document.Tools.Should().ContainSingle().Subject;
            tool.Name.Should().Be("get_customer_context");
            tool.Params["detail"].Values.Should().Equal("summary", "full");
            tool.Root.Fields.Should().Equal("id", "name");
            tool.Include.Should().ContainSingle().Which.Aggregate!.Sum.Should().Be("total");
            tool.Policy.AllowedRoles.Should().Equal("support");
        }

        /// <summary>
        /// 'hiddenFieldBehavior' is parsed and schema-validated but NOTHING enforces it.
        /// A security-shaped setting that reads as active and does nothing is worse than
        /// no setting, so declaring it must fail the load with a message naming the real
        /// behaviour — never be accepted and silently ignored.
        /// </summary>
        [Fact]
        public async Task Loader_RejectsHiddenFieldBehavior_BecauseNothingEnforcesIt()
        {
            var json = ValidDocument.Replace(
                "\"policy\": { \"allowedRoles\"",
                "\"policy\": { \"hiddenFieldBehavior\": \"omit\", \"allowedRoles\"");

            var act = () => LoadAsync(json);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*hiddenFieldBehavior*reserved and NOT enforced*");
        }

        [Theory]
        [InlineData("\"support\"", "*must be an array*")]
        [InlineData("[\"\"]", "*non-empty role names*")]
        public async Task Loader_RejectsMalformedAllowedRoles(string value, string expected)
        {
            var json = ValidDocument.Replace("[\"support\"]", value);

            var act = () => LoadAsync(json);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);
        }

        [Theory]
        [InlineData("{ \"tools\": [] }", "missing required property 'version'")]
        [InlineData("{ \"version\": 2, \"tools\": [] }", "Unsupported declarative MCP tool document version '2'")]
        public async Task Loader_RejectsMissingOrUnknownVersion(string json, string expected)
        {
            var act = () => LoadAsync(json);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{expected}*");
        }

        [Fact]
        public async Task Loader_RejectsUnknownPropertyWithToolNameAndKey()
        {
            var json = ValidDocument.Replace("\"description\": \"Return", "\"descriptoin\": \"typo\", \"description\": \"Return");

            var act = () => LoadAsync(json);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Unknown property 'descriptoin' in tool 'get_customer_context'*");
        }

        [Theory]
        [InlineData("")]
        [InlineData("short")]
        public async Task Loader_RejectsMissingOrTooShortDescription(string description)
        {
            var json = ValidDocument.Replace("Return a customer and related order summary.", description);

            var act = () => LoadAsync(json);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*get_customer_context*description*");
        }

        [Fact]
        public async Task Loader_WarnsWithToolAndParameterWhenParameterDescriptionIsMissing()
        {
            var warnings = new List<string>();

            _ = await new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(Stream(ValidDocument), "test document"),
                warnings.Add).LoadAsync(CancellationToken.None);

            warnings.Should().Contain(message => message.Contains("get_customer_context") && message.Contains("customerId"));
        }

        [Fact]
        public async Task Loader_RejectsIncompatibleDetailParameterWithDetailGating()
        {
            var invalid = ValidDocument.Replace("[\"summary\", \"full\"]", "[\"brief\", \"verbose\"]");
            var act = () => LoadAsync(invalid);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*get_customer_context*detail*reserved*");
        }

        [Fact]
        public async Task Registration_ExposesTheDocumentThroughTheProvider_AsAnIndependentService()
        {
            var services = new ServiceCollection();
            services.AddBifrostMcpTools(Stream(ValidDocument), "test document");

            await using var provider = services.BuildServiceProvider();
            (await provider.GetRequiredService<DeclarativeToolDocumentProvider>().GetAsync(CancellationToken.None))
                .Tools.Should().ContainSingle();
            provider.GetService<AppMetadataModel>().Should().BeNull();
            provider.GetService<IDbModel>().Should().BeNull();
        }

        [Fact]
        public async Task BrokenDocument_FailsTheHostStart_BeforeAnyToolIsServed()
        {
            var services = new ServiceCollection();
            services.AddSingleton<BifrostQL.Core.Resolvers.IQueryIntentExecutor>(new ThrowingExecutor());
            services.AddBifrostMcpTools(Stream("{ bad json"), "broken document");
            await using var provider = services.BuildServiceProvider();

            var start = async () =>
            {
                foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
                    await hosted.StartAsync(CancellationToken.None);
            };

            await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*broken document*");
        }

        [Fact]
        public async Task FileRegistration_ReadsNothingAtRegistration_AndReadsTheFileAsynchronouslyOnFirstUse()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bifrost-mcp-tools-{Guid.NewGuid():N}.json");
            try
            {
                var services = new ServiceCollection();

                // Registration runs before the file exists: it must not touch the file system.
                services.AddBifrostMcpTools(path);
                await File.WriteAllTextAsync(path, ValidDocument);

                await using var provider = services.BuildServiceProvider();
                (await provider.GetRequiredService<DeclarativeToolDocumentProvider>().GetAsync(CancellationToken.None))
                    .Version.Should().Be(1);

                await using var stream = await new FileDeclarativeToolDocumentSource(path).OpenReadAsync(CancellationToken.None);
                stream.Should().BeOfType<FileStream>().Which.IsAsync.Should().BeTrue(
                    "the document is read with async I/O, never a synchronous read on a pool thread");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Provider_ReadsTheDocumentOnce_AcrossConcurrentCallers()
        {
            var source = new CountingSource(ValidDocument);
            var documents = new DeclarativeToolDocumentProvider(new DeclarativeToolDocumentLoader(source));

            var loaded = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => documents.GetAsync(CancellationToken.None))));

            source.Opens.Should().Be(1);
            loaded.Distinct().Should().ContainSingle("every caller shares the one loaded document");
        }

        private sealed class CountingSource(string json) : IDeclarativeToolDocumentSource
        {
            private int _opens;
            public int Opens => Volatile.Read(ref _opens);
            public string Description => "counting source";

            public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _opens);
                return ValueTask.FromResult<Stream>(Stream(json));
            }
        }

        private sealed class ThrowingExecutor : BifrostQL.Core.Resolvers.IQueryIntentExecutor
        {
            public Task<IDbModel> GetModelAsync(string? endpoint = null) =>
                throw new InvalidOperationException("control: the document load must fail before the model is read");

            public Task<BifrostQL.Core.Resolvers.QueryIntentResult> ExecuteAsync(
                BifrostQL.Core.Resolvers.QueryIntent intent, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private static Task<DeclarativeToolDocument> LoadAsync(string json) =>
            new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(Stream(json), "test document")).LoadAsync(CancellationToken.None);

        private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
    }
}
