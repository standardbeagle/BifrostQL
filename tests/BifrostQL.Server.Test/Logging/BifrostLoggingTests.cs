using GraphQL;
using GraphQL.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
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
            var config = new BifrostLoggingConfiguration();
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
            var config = new BifrostLoggingConfiguration();
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

        [Fact]
        public void LoggingModule_LogsTheInnerException_SoTheFaultStackIsKept()
        {
            var (module, _, exceptions) = CreateModuleWithExceptions();
            var cause = new InvalidOperationException("driver detail");

            module.HandleGraphQLError(new ExecutionError("An unhandled error has occurred.", cause));

            Assert.Single(exceptions);
            Assert.Same(cause, exceptions[0]);
        }

        [Fact]
        public async System.Threading.Tasks.Task SlowQueryWarning_OmitsSql_UnlessLogSqlIsOn()
        {
            var messages = new List<(LogLevel Level, string Message)>();
            var factory = new LoggerFactory();
            factory.AddProvider(new TestLoggerProvider(messages));
            var context = SlowQueryContext("SELECT secret_column FROM t");

            var quiet = new QueryLoggingObserver(factory.CreateLogger<QueryLoggingObserver>(),
                new BifrostLoggingConfiguration { LogSql = false, SlowQueryThresholdMs = 0 });
            await quiet.OnQueryPhaseAsync(BifrostQL.Core.Modules.QueryPhase.AfterExecute, context);

            var verbose = new QueryLoggingObserver(factory.CreateLogger<QueryLoggingObserver>(),
                new BifrostLoggingConfiguration { LogSql = true, SlowQueryThresholdMs = 0 });
            await verbose.OnQueryPhaseAsync(BifrostQL.Core.Modules.QueryPhase.AfterExecute, context);

            Assert.Equal(2, messages.Count);
            Assert.All(messages, m => Assert.Equal(LogLevel.Warning, m.Level));
            Assert.DoesNotContain("secret_column", messages[0].Message);
            // Control: the same context does carry the SQL when LogSql is on.
            Assert.Contains("secret_column", messages[1].Message);
        }

        private static BifrostQL.Core.Modules.QueryObserverContext SlowQueryContext(string sql)
        {
            var table = NSubstitute.Substitute.For<BifrostQL.Core.Model.IDbTable>();
            table.DbName.Returns("t");
            return new BifrostQL.Core.Modules.QueryObserverContext
            {
                Table = table,
                Sql = sql,
                Duration = TimeSpan.FromSeconds(5),
                UserContext = new Dictionary<string, object?>(),
                Model = NSubstitute.Substitute.For<BifrostQL.Core.Model.IDbModel>(),
                QueryType = default,
                Path = "t",
            };
        }

        private static (BifrostLoggingModule Module, List<(LogLevel Level, string Message)> Messages, List<Exception> Exceptions) CreateModuleWithExceptions()
        {
            var messages = new List<(LogLevel Level, string Message)>();
            var exceptions = new List<Exception>();
            var factory = new LoggerFactory();
            factory.AddProvider(new TestLoggerProvider(messages, exceptions));
            return (new BifrostLoggingModule(factory.CreateLogger<BifrostLoggingModule>(), new BifrostLoggingConfiguration()), messages, exceptions);
        }

        private static (BifrostLoggingModule Module, List<(LogLevel Level, string Message)> Messages) CreateModule()
        {
            var logMessages = new List<(LogLevel Level, string Message)>();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new TestLoggerProvider(logMessages));
            var logger = loggerFactory.CreateLogger<BifrostLoggingModule>();
            var module = new BifrostLoggingModule(logger, new BifrostLoggingConfiguration());
            return (module, logMessages);
        }
    }

    public class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _logMessages;
        private readonly List<Exception>? _exceptions;

        public TestLoggerProvider(List<(LogLevel Level, string Message)> logMessages, List<Exception>? exceptions = null)
        {
            _logMessages = logMessages;
            _exceptions = exceptions;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestLogger(_logMessages, _exceptions);
        }

        public void Dispose() { }
    }

    internal class TestLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logMessages;
        private readonly List<Exception>? _exceptions;

        public TestLogger(List<(LogLevel Level, string Message)> logMessages, List<Exception>? exceptions = null)
        {
            _logMessages = logMessages;
            _exceptions = exceptions;
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
            if (exception != null)
                _exceptions?.Add(exception);
        }
    }
}