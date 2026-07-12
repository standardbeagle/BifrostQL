using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Maps the BifrostQL chat endpoints (<see cref="BifrostChatMiddleware"/>):
        /// <c>POST {Path}/conversations</c> and
        /// <c>POST {Path}/conversations/{id}/messages</c> (SSE streaming). Opt-in —
        /// call after <c>AddBifrostEndpoints</c>/<c>AddBifrostQL</c> and after
        /// <c>UseAuthentication</c> in the pipeline, mirroring
        /// <see cref="UseBifrostAppMetadata"/>.
        ///
        /// Fails fast at startup, not on the first user request: the options are
        /// validated here and <see cref="IChatCompletionService"/> is resolved
        /// eagerly, so a deployment with chat endpoints registered but no Anthropic
        /// api key configured refuses to start.
        /// </summary>
        public static IApplicationBuilder UseBifrostChat(
            this IApplicationBuilder app,
            Action<BifrostChatOptions>? configure = null)
        {
            var options = new BifrostChatOptions();
            configure?.Invoke(options);
            if (!options.Enabled)
                return app;

            if (string.IsNullOrWhiteSpace(options.Path) || !options.Path.StartsWith('/'))
                throw new InvalidOperationException(
                    "BifrostChatOptions.Path must be a non-empty path starting with '/'.");
            if (options.HistoryLimit < 1)
                throw new InvalidOperationException(
                    "BifrostChatOptions.HistoryLimit must be at least 1.");

            var services = app.ApplicationServices;
            if (services.GetService<IQueryIntentExecutor>() is null
                || services.GetService<IMutationIntentExecutor>() is null)
                throw new InvalidOperationException(
                    "The intent executors are not registered. Call AddBifrostEndpoints (or AddBifrostQL) before UseBifrostChat.");

            // Startup health gate: constructing the completion service performs its
            // fail-fast configuration checks (e.g. the Anthropic api-key check).
            _ = services.GetRequiredService<IChatCompletionService>();

            return app.UseMiddleware<BifrostChatMiddleware>(options);
        }
    }
}
