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

        public static IServiceCollection AddBifrostMcpTools(
            this IServiceCollection services,
            Stream stream,
            string description = "stream")
            => services.AddBifrostMcpTools(new StreamDeclarativeToolDocumentSource(stream, description));

        public static IServiceCollection AddBifrostMcpTools(
            this IServiceCollection services,
            IDeclarativeToolDocumentSource source)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(source);

            var loader = new DeclarativeToolDocumentLoader(source);
            var document = loader.Load();
            services.AddSingleton(source);
            services.AddSingleton(loader);
            services.AddSingleton(document);
            services.AddSingleton<DeclarativeToolDocumentValidator>();
            services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<DeclarativeToolDocumentValidator>());
            return services;
        }
    }
}
