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

        [Fact]
        public void LoggingModule_OperationCanceledCode_LogsAsDebug()
        {
            // Arrange — GraphQL.NET surfaces a canceled resolver as an ExecutionError
            // with code OPERATION_CANCELED; a client abort is routine, not a fault.
            var (module, logMessages) = CreateModule();
            var canceledError = new ExecutionError("The operation was canceled.")
            {
                Code = "OPERATION_CANCELED"
            };

            // Act
            module.HandleGraphQLError(canceledError);

            // Assert
            Assert.Single(logMessages);
            Assert.Equal(LogLevel.Debug, logMessages[0].Level);
        }

        [Fact]
        public void LoggingModule_WrappedOperationCanceledException_LogsAsDebug()
        {
            // Arrange — a cancellation wrapped into an ExecutionError without the code
            // (e.g. by intermediate error handling) must also stay quiet.
            var (module, logMessages) = CreateModule();
            var wrapped = new ExecutionError("Canceled", new OperationCanceledException());

            // Act
            module.HandleGraphQLError(wrapped);

            // Assert
            Assert.Single(logMessages);
            Assert.Equal(LogLevel.Debug, logMessages[0].Level);
        }

        [Fact]
        public async System.Threading.Tasks.Task ErrorLoggingMiddleware_ClientAbortCancellation_DoesNotLogError()
        {
            // Arrange — the execution's own token is canceled (client abort) and the
            // pipeline throws OperationCanceledException: the middleware must rethrow
            // without producing an error-level "unhandled error" log entry.
            var (module, logMessages) = CreateModule();
            var middleware = new BifrostErrorLoggingMiddleware(module);
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            var options = new ExecutionOptions { CancellationToken = cts.Token };

            // Act / Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                middleware.ExecuteAsync(options, _ => throw new OperationCanceledException(cts.Token)));
            Assert.DoesNotContain(logMessages, m => m.Level >= LogLevel.Warning);

            // A cancellation thrown WITHOUT the token being canceled is a real fault:
            // it must NOT be swallowed by the client-abort filter — it flows through the
            // generic catch (logged here) and rethrows, so BifrostHttpMiddleware's
            // generic catch (request not aborted) logs it at Error with a stack trace.
            var faultOptions = new ExecutionOptions { CancellationToken = default };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                middleware.ExecuteAsync(faultOptions, _ => throw new OperationCanceledException()));
            Assert.Contains(logMessages, m => m.Message.Contains("An unhandled error has occurred."));

            // Non-cancellation exceptions stay at error level, unchanged.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                middleware.ExecuteAsync(faultOptions, _ => throw new InvalidOperationException("boom")));
            Assert.Contains(logMessages, m => m.Level == LogLevel.Error);
        }

        private static (BifrostLoggingModule Module, List<(LogLevel Level, string Message)> Messages) CreateModule()
        {
            var logMessages = new List<(LogLevel Level, string Message)>();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new TestLoggerProvider(logMessages));
            var logger = loggerFactory.CreateLogger<BifrostLoggingModule>();
            var module = new BifrostLoggingModule(logger, new BifrostLoggingConfiguration
            {
                EnableConsole = true,
                EnableFile = false,
                MinimumLevel = LogLevel.Debug
            });
            return (module, logMessages);
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