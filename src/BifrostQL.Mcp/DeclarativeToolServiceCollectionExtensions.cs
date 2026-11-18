using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Mcp
{
    public static class DeclarativeToolServiceCollectionExtensions
    {
        public static IServiceCollection AddBifrostMcpTools(
            this IServiceCollection services,
            string filePath)
            => services.AddBifrostMcpTools(new FileDeclarativeToolDocumentSource(filePath));

        /// <summary>
        /// Registers a document read from a stream <paramref name="openStream"/> opens at host start.
        /// Nothing is read at registration; the loader disposes the stream.
        /// </summary>
        public static IServiceCollection AddBifrostMcpTools(
            this IServiceCollection services,
            Func<CancellationToken, Task<Stream>> openStream,
            string description = "stream")
            => services.AddBifrostMcpTools(new StreamDeclarativeToolDocumentSource(openStream, description));

        public static IServiceCollection AddBifrostMcpTools(
            this IServiceCollection services,
            IDeclarativeToolDocumentSource source)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(source);

            // Nothing is read here: registration is synchronous, and the document is read with
            // async I/O by the provider. The validator's StartAsync forces that read, so a missing
            // or malformed document still fails the host start, before any tool is served.
            services.AddSingleton(source);
            services.AddSingleton(new DeclarativeToolDocumentLoader(source));
            services.AddSingleton<DeclarativeToolDocumentProvider>();
            services.AddSingleton<DeclarativeToolDocumentValidator>();
            services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<DeclarativeToolDocumentValidator>());
            return services;
        }
    }
}
