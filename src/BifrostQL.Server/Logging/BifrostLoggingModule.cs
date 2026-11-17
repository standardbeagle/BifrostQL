using GraphQL;
using GraphQL.Validation;
using GraphQL.Types;
using GraphQL.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Collections.Generic;

namespace BifrostQL.Server.Logging
{
    /// <summary>
    /// Settings read by Bifrost's own GraphQL error logging and query observer. Sinks, files
    /// and minimum levels belong to the host's logging configuration (<c>Logging:LogLevel</c>,
    /// a provider of its choice), not to this type.
    /// </summary>
    public class BifrostLoggingConfiguration
    {
        public bool EnableQueryLogging { get; set; } = true;
        public int SlowQueryThresholdMs { get; set; } = 1000;
        public bool LogSql { get; set; } = false;
    }

    public class BifrostLoggingModule
    {
        private readonly ILogger<BifrostLoggingModule> _logger;
        private readonly BifrostLoggingConfiguration _config;

        public BifrostLoggingModule(ILogger<BifrostLoggingModule> logger, BifrostLoggingConfiguration? config = null)
        {
            _logger = logger;
            _config = config ?? new BifrostLoggingConfiguration();
        }

        public void HandleGraphQLError(ExecutionError error)
        {
            var logLevel = DetermineLogLevel(error);
            var data = new Dictionary<string, object?>
            {
                ["code"] = error.Code,
                ["path"] = error.Path?.Any() == true ? string.Join("/", error.Path) : null,
                ["locations"] = error.Locations?.Select(l => $"{l.Line}:{l.Column}").ToArray(),
                ["data"] = error.Data
            };

            // The inner exception carries the stack and the driver/transformer detail; without
            // it a resolver fault logged only its wrapper message.
            _logger.Log(
                logLevel,
                error.InnerException,
                "GraphQL Error: {Message} Details: {@Details}",
                error.Message,
                data
            );
        }

        private static LogLevel DetermineLogLevel(ExecutionError error)
        {
            // A canceled operation (client abort surfacing as GraphQL.NET's
            // OPERATION_CANCELED error, or an OperationCanceledException wrapped in an
            // ExecutionError) is routine, not a server fault — log quietly.
            if (error.Code == "OPERATION_CANCELED" || error.InnerException is OperationCanceledException)
                return LogLevel.Debug;

            return error switch
            {
                ValidationError or DocumentError or SyntaxError => LogLevel.Warning,
                ExecutionError err => err.Code switch
                {
                    "AUTHORIZATION_ERROR" => LogLevel.Warning,
                    "SCHEMA_ERROR" => LogLevel.Error,
                    _ => LogLevel.Error
                },
                _ => LogLevel.Error
            };
        }
    }

    public static class BifrostLoggingExtensions
    {
        public static IServiceCollection AddBifrostLogging(
            this IServiceCollection services,
            Action<BifrostLoggingConfiguration>? configureOptions = null)
        {
            var config = new BifrostLoggingConfiguration();
            configureOptions?.Invoke(config);

            services.AddSingleton(config);
            services.AddSingleton<BifrostLoggingModule>();

            return services;
        }
    }
}