using GraphQL;
using GraphQL.DI;
using GraphQL.Execution;
using GraphQL.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace BifrostQL.Server.Logging
{
    public static class BifrostGraphQLBuilderExtensions
    {
        public static IGraphQLBuilder AddBifrostErrorLogging(
            this IGraphQLBuilder builder,
            Action<BifrostLoggingConfiguration>? configureOptions = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            // Add logging services
            var services = builder.Services as IServiceCollection
                ?? throw new InvalidOperationException("GraphQLBuilder.Services must be of type IServiceCollection");

            services.AddBifrostLogging(configureOptions);

            // Add middleware for error handling
            services.AddSingleton<IConfigureExecution, BifrostErrorLoggingMiddleware>();

            return builder;
        }
    }

    internal class BifrostErrorLoggingMiddleware : IConfigureExecution
    {
        private readonly BifrostLoggingModule _loggingModule;

        public BifrostErrorLoggingMiddleware(BifrostLoggingModule loggingModule)
        {
            _loggingModule = loggingModule ?? throw new ArgumentNullException(nameof(loggingModule));
        }

        // IConfigureExecution interface implementation
        public float SortOrder => 1000.0f; // Run after most other middleware

        public async Task<ExecutionResult> ExecuteAsync(ExecutionOptions options, ExecutionDelegate next)
        {
            try
            {
                var result = await next(options);

                if (result.Errors?.Count > 0)
                {
                    foreach (var error in result.Errors)
                    {
                        _loggingModule.HandleGraphQLError(error);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                var error = new ExecutionError("An unhandled error has occurred.", ex);
                _loggingModule.HandleGraphQLError(error);
                throw;
            }
        }
    }
}