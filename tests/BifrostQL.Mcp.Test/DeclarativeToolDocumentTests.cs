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
                "policy": { "hiddenFieldBehavior": "omit", "allowedRoles": ["support"] }
              }]
            }
            """;

        [Fact]
        public void Loader_DeserializesVersionedTypedShape()
        {
            var document = Load(ValidDocument);

            document.Version.Should().Be(1);
            var tool = document.Tools.Should().ContainSingle().Subject;
            tool.Name.Should().Be("get_customer_context");
            tool.Params["detail"].Values.Should().Equal("summary", "full");
            tool.Root.Fields.Should().Equal("id", "name");
            tool.Include.Should().ContainSingle().Which.Aggregate!.Sum.Should().Be("total");
            tool.Policy.AllowedRoles.Should().Equal("support");
        }

        [Theory]
        [InlineData("{ \"tools\": [] }", "missing required property 'version'")]
        [InlineData("{ \"version\": 2, \"tools\": [] }", "Unsupported declarative MCP tool document version '2'")]
        public void Loader_RejectsMissingOrUnknownVersion(string json, string expected)
        {
            var act = () => Load(json);

            act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
        }

        [Fact]
        public void Loader_RejectsUnknownPropertyWithToolNameAndKey()
        {
            var json = ValidDocument.Replace("\"description\": \"Return", "\"descriptoin\": \"typo\", \"description\": \"Return");

            var act = () => Load(json);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Unknown property 'descriptoin' in tool 'get_customer_context'*");
        }

        [Theory]
        [InlineData("")]
        [InlineData("short")]
        public void Loader_RejectsMissingOrTooShortDescription(string description)
        {
            var json = ValidDocument.Replace("Return a customer and related order summary.", description);

            var act = () => Load(json);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*get_customer_context*description*");
        }

        [Fact]
        public void Loader_WarnsWithToolAndParameterWhenParameterDescriptionIsMissing()
        {
            var warnings = new List<string>();

            _ = new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(Stream(ValidDocument), "test document"),
                warnings.Add).Load();

            warnings.Should().Contain(message => message.Contains("get_customer_context") && message.Contains("customerId"));
        }

        [Fact]
        public void Loader_RejectsIncompatibleDetailParameterWithDetailGating()
        {
            var invalid = ValidDocument.Replace("[\"summary\", \"full\"]", "[\"brief\", \"verbose\"]");
            var act = () => Load(invalid);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*get_customer_context*detail*reserved*");
        }

        [Fact]
        public void Registration_LoadsImmediatelyAndExposesIndependentService()
        {
            var services = new ServiceCollection();
            services.AddBifrostMcpTools(Stream(ValidDocument), "test document");

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<DeclarativeToolDocument>().Tools.Should().ContainSingle();
            provider.GetService<AppMetadataModel>().Should().BeNull();
            provider.GetService<IDbModel>().Should().BeNull();
        }

        [Fact]
        public void Registration_FailsFastInsteadOfRegisteringEmptyDocument()
        {
            var services = new ServiceCollection();
            var act = () => services.AddBifrostMcpTools(Stream("{ bad json"), "broken document");

            act.Should().Throw<InvalidOperationException>().WithMessage("*broken document*");
            services.Should().BeEmpty();
        }

        [Fact]
        public void FileRegistration_LoadsDocumentAtRegistrationTime()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, ValidDocument);
                var services = new ServiceCollection();

                services.AddBifrostMcpTools(path);
                File.Delete(path);

                using var provider = services.BuildServiceProvider();
                provider.GetRequiredService<DeclarativeToolDocument>().Version.Should().Be(1);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static DeclarativeToolDocument Load(string json) =>
            new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(Stream(json), "test document")).Load();

        private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
    }
}
