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
    public class BifrostLoggingConfiguration
    {
        public string? LogFilePath { get; set; }
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
        public bool EnableConsole { get; set; } = true;
        public bool EnableFile { get; set; } = true;
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

        public void HandleGraphQLError(ExecutionError error, IDictionary<string, object?>? userContext = null)
        {
            var logLevel = DetermineLogLevel(error);
            var data = new Dictionary<string, object?>
            {
                ["code"] = error.Code,
                ["path"] = error.Path?.Any() == true ? string.Join("/", error.Path) : null,
                ["locations"] = error.Locations?.Select(l => $"{l.Line}:{l.Column}").ToArray(),
                ["data"] = error.Data
            };

            if (userContext != null)
            {
                data["userContext"] = userContext;
            }

            _logger.Log(
                logLevel,
                "GraphQL Error: {Message} Details: {@Details}",
                error.Message,
                data
            );
        }

        private static LogLevel DetermineLogLevel(ExecutionError error)
        {
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