using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace BifrostQL.Server
{
    /// <summary>
    /// Application builder extensions for hosting a single-page application alongside a
    /// BifrostQL GraphQL endpoint in the same process (same-origin GraphQL).
    /// </summary>
    public static class SpaHostingExtensions
    {
        /// <summary>
        /// Serves static SPA assets and adds an <c>index.html</c> route fallback that does
        /// not shadow GraphQL, the GraphQL playground, health, or <c>/api</c> routes.
        /// Call after <c>UseBifrostQL</c> so the GraphQL endpoint is registered first.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="configure">Optional configuration for asset directory and excluded prefixes.</param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostSpa(
            this IApplicationBuilder app,
            Action<BifrostSpaOptions>? configure = null)
        {
            var options = new BifrostSpaOptions();
            configure?.Invoke(options);

            var fileOptions = BuildFileOptions(app, options);

            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = fileOptions.FileProvider,
            });
            app.UseStaticFiles(fileOptions);

            app.MapWhen(
                context => !IsExcludedFromSpaFallback(context.Request.Path, options),
                branch => branch.UseSpaIndexFallback(fileOptions.FileProvider!));

            return app;
        }

        /// <summary>
        /// Determines whether <paramref name="path"/> should bypass the SPA <c>index.html</c>
        /// fallback because it matches one of the configured excluded prefixes.
        /// Matching is case-insensitive and respects path-segment boundaries so that
        /// <c>/apixyz</c> is not treated as being under the <c>/api</c> prefix.
        /// </summary>
        public static bool IsExcludedFromSpaFallback(string path, BifrostSpaOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var requestPath = string.IsNullOrEmpty(path) ? "/" : path;

            foreach (var prefix in options.ExcludedPathPrefixes)
            {
                if (string.IsNullOrEmpty(prefix)) continue;

                if (prefix == "/")
                {
                    // The root prefix matches only the exact root request; treating it as a
                    // catch-all would steal every SPA route.
                    if (requestPath == "/") return true;
                    continue;
                }

                if (!requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Exact match, or the next character is a segment separator.
                if (requestPath.Length == prefix.Length || requestPath[prefix.Length] == '/')
                    return true;
            }

            return false;
        }

        private static StaticFileOptions BuildFileOptions(IApplicationBuilder app, BifrostSpaOptions options)
        {
            IFileProvider fileProvider;

            if (string.IsNullOrWhiteSpace(options.AssetDirectory))
            {
                var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
                if (env.WebRootFileProvider == null)
                    throw new InvalidOperationException(
                        "No SPA AssetDirectory configured and the host has no web root. " +
                        "Set BifrostSpaOptions.AssetDirectory.");
                fileProvider = env.WebRootFileProvider;
            }
            else
            {
                var fullPath = Path.GetFullPath(options.AssetDirectory);
                if (!Directory.Exists(fullPath))
                    throw new DirectoryNotFoundException(
                        $"SPA asset directory '{fullPath}' does not exist.");
                fileProvider = new PhysicalFileProvider(fullPath);
            }

            return new StaticFileOptions
            {
                FileProvider = fileProvider,
            };
        }

        private static void UseSpaIndexFallback(this IApplicationBuilder branch, IFileProvider fileProvider)
        {
            branch.Run(async context =>
            {
                var indexFile = fileProvider.GetFileInfo("index.html");
                if (!indexFile.Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(indexFile, context.RequestAborted);
            });
        }
    }
}
