using GraphQL;
using GraphQL.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using BifrostQL.Server.Logging;

namespace BifrostQL.Server.Test.Logging
{
    public class BifrostLoggingTests
    {
        [Fact]
        public void LoggingModule_HandleGraphQLError_LogsErrorCorrectly()
        {
            // Arrange
            var logMessages = new List<(LogLevel Level, string Message)>();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new TestLoggerProvider(logMessages));

            var logger = loggerFactory.CreateLogger<BifrostLoggingModule>();
            var config = new BifrostLoggingConfiguration
            {
                EnableConsole = true,
                EnableFile = false,
                MinimumLevel = LogLevel.Debug
            };
            var module = new BifrostLoggingModule(logger, config);

            var executionError = new ExecutionError("Test error message")
            {
                Code = "TEST_ERROR",
                Path = new[] { "query", "field" }
            };

            // Act
            module.HandleGraphQLError(executionError);

            // Assert
            Assert.Single(logMessages);
            var (level, message) = logMessages[0];
            Assert.Equal(LogLevel.Error, level);
            Assert.Contains("Test error message", message);
            Assert.Contains("TEST_ERROR", message);
            Assert.Contains("query/field", message);
        }

        [Fact]
        public void LoggingModule_HandleValidationError_LogsAsWarning()
        {
            // Arrange
            var logMessages = new List<(LogLevel Level, string Message)>();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new TestLoggerProvider(logMessages));

            var logger = loggerFactory.CreateLogger<BifrostLoggingModule>();
            var config = new BifrostLoggingConfiguration
            {
                EnableConsole = true,
                EnableFile = false,
                MinimumLevel = LogLevel.Debug
            };
            var module = new BifrostLoggingModule(logger, config);

            var validationError = new ValidationError("Test validation error");

            // Act
            module.HandleGraphQLError(validationError);

            // Assert
            Assert.Single(logMessages);
            var (level, message) = logMessages[0];
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains("Test validation error", message);
        }
    }

    public class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _logMessages;

        public TestLoggerProvider(List<(LogLevel Level, string Message)> logMessages)
        {
            _logMessages = logMessages;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestLogger(_logMessages);
        }

        public void Dispose() { }
    }

    internal class TestLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logMessages;

        public TestLogger(List<(LogLevel Level, string Message)> logMessages)
        {
            _logMessages = logMessages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            _logMessages.Add((logLevel, message));
        }
    }
}