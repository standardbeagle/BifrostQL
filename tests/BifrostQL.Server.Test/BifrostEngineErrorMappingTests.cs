using System.Text;
using System.Text.Json;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The protocol-independent <see cref="BifrostEngine"/> (binary WS, frontend mounts) and the
/// HTTP <see cref="BifrostDocumentExecutor"/> run one copy of the pre-execution glue. The engine
/// used to let an endpoint-load fault escape to the host, so a binary client saw the raw driver
/// text (here a host name) or an opaque transport failure instead of the HTTP constant.
/// </summary>
public sealed class BifrostEngineErrorMappingTests
{
    private const string DriverText = "A network-related error occurred connecting to db-internal-7.corp:1433";

    private static ServiceProvider Services()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader("/graphql", () => throw new InvalidOperationException(DriverText));
        var filterTransformers = new FilterTransformersWrap { Transformers = Array.Empty<IFilterTransformer>() };
        var services = new ServiceCollection();
        services.AddSingleton(pathCache);
        services.AddSingleton<IFilterTransformers>(filterTransformers);
        services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Engine_EndpointLoadFault_ReturnsTheHttpConstant_AndKeepsTheDriverTextOffTheWire()
    {
        await using var services = Services();
        var engine = new BifrostEngine(new DocumentExecuter(), services.GetRequiredService<PathCache<Inputs>>());

        var result = await engine.ExecuteAsync(
            new BifrostRequest { Query = "{ __typename }", RequestServices = services }, "/graphql");

        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Be("Database connection failed.");
        result.Errors[0].Message.Should().NotContain("db-internal-7");
    }

    [Fact]
    public async Task Engine_AndHttpMiddleware_AnswerTheSameFaultWithTheSameText()
    {
        await using var services = Services();
        var engine = new BifrostEngine(new DocumentExecuter(), services.GetRequiredService<PathCache<Inputs>>());
        var engineResult = await engine.ExecuteAsync(
            new BifrostRequest { Query = "{ __typename }", RequestServices = services }, "/graphql");

        var middleware = new BifrostHttpMiddleware(
            next: _ => Task.CompletedTask,
            serializer: new GraphQLSerializer(),
            documentExecutor: new DocumentExecuter(),
            logger: NullLogger<BifrostHttpMiddleware>.Instance);
        var context = new DefaultHttpContext { RequestServices = services };
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        context.Request.Method = HttpMethods.Post;
        context.Request.PathBase = "/graphql";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { query = "{ __typename }" })));
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        var httpMessage = doc.RootElement.GetProperty("errors")[0].GetProperty("message").GetString();

        httpMessage.Should().Be("Database connection failed.");
        engineResult.Errors.Should().ContainSingle().Which.Message.Should().Be(httpMessage);
    }
}
