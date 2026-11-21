using GraphQL;
using GraphQL.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
            var logger = new FakeLogger<BifrostLoggingModule>();
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
            var record = Assert.Single(logger.Collector.GetSnapshot());
            Assert.Equal(LogLevel.Error, record.Level);
            Assert.Contains("Test error message", record.Message);
            Assert.Contains("TEST_ERROR", record.Message);
            Assert.Contains("query/field", record.Message);
        }

        [Fact]
        public void LoggingModule_HandleValidationError_LogsAsWarning()
        {
            // Arrange
            var logger = new FakeLogger<BifrostLoggingModule>();
            var config = new BifrostLoggingConfiguration();
            var module = new BifrostLoggingModule(logger, config);

            var validationError = new ValidationError("Test validation error");

            // Act
            module.HandleGraphQLError(validationError);

            // Assert
            var record = Assert.Single(logger.Collector.GetSnapshot());
            Assert.Equal(LogLevel.Warning, record.Level);
            Assert.Contains("Test validation error", record.Message);
        }

        [Fact]
        public void LoggingModule_OperationCanceledCode_LogsAsDebug()
        {
            // Arrange — GraphQL.NET surfaces a canceled resolver as an ExecutionError
            // with code OPERATION_CANCELED; a client abort is routine, not a fault.
            var (module, log) = CreateModule();
            var canceledError = new ExecutionError("The operation was canceled.")
            {
                Code = "OPERATION_CANCELED"
            };

            // Act
            module.HandleGraphQLError(canceledError);

            // Assert
            Assert.Equal(LogLevel.Debug, Assert.Single(log.GetSnapshot()).Level);
        }

        [Fact]
        public void LoggingModule_WrappedOperationCanceledException_LogsAsDebug()
        {
            // Arrange — a cancellation wrapped into an ExecutionError without the code
            // (e.g. by intermediate error handling) must also stay quiet.
            var (module, log) = CreateModule();
            var wrapped = new ExecutionError("Canceled", new OperationCanceledException());

            // Act
            module.HandleGraphQLError(wrapped);

            // Assert
            Assert.Equal(LogLevel.Debug, Assert.Single(log.GetSnapshot()).Level);
        }

        [Fact]
        public async System.Threading.Tasks.Task ErrorLoggingMiddleware_ClientAbortCancellation_DoesNotLogError()
        {
            // Arrange — the execution's own token is canceled (client abort) and the
            // pipeline throws OperationCanceledException: the middleware must rethrow
            // without producing an error-level "unhandled error" log entry.
            var (module, log) = CreateModule();
            var middleware = new BifrostErrorLoggingMiddleware(module);
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            var options = new ExecutionOptions { CancellationToken = cts.Token };

            // Act / Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                middleware.ExecuteAsync(options, _ => throw new OperationCanceledException(cts.Token)));
            Assert.DoesNotContain(log.GetSnapshot(), m => m.Level >= LogLevel.Warning);

            // A cancellation thrown WITHOUT the token being canceled is a real fault:
            // it must NOT be swallowed by the client-abort filter — it flows through the
            // generic catch (logged here) and rethrows, so BifrostHttpMiddleware's
            // generic catch (request not aborted) logs it at Error with a stack trace.
            var faultOptions = new ExecutionOptions { CancellationToken = default };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                middleware.ExecuteAsync(faultOptions, _ => throw new OperationCanceledException()));
            Assert.Contains(log.GetSnapshot(), m => m.Message.Contains("An unhandled error has occurred."));

            // Non-cancellation exceptions stay at error level, unchanged.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                middleware.ExecuteAsync(faultOptions, _ => throw new InvalidOperationException("boom")));
            Assert.Contains(log.GetSnapshot(), m => m.Level == LogLevel.Error);
        }

        [Fact]
        public void LoggingModule_LogsTheInnerException_SoTheFaultStackIsKept()
        {
            var (module, log) = CreateModule();
            var cause = new InvalidOperationException("driver detail");

            module.HandleGraphQLError(new ExecutionError("An unhandled error has occurred.", cause));

            Assert.Same(cause, Assert.Single(log.GetSnapshot(), r => r.Exception is not null).Exception);
        }

        [Fact]
        public async System.Threading.Tasks.Task SlowQueryWarning_OmitsSql_UnlessLogSqlIsOn()
        {
            var log = new FakeLogCollector();
            var context = SlowQueryContext("SELECT secret_column FROM t");

            var quiet = new QueryLoggingObserver(new FakeLogger<QueryLoggingObserver>(log),
                new BifrostLoggingConfiguration { LogSql = false, SlowQueryThresholdMs = 0 });
            await quiet.OnQueryPhaseAsync(BifrostQL.Core.Modules.QueryPhase.AfterExecute, context);

            var verbose = new QueryLoggingObserver(new FakeLogger<QueryLoggingObserver>(log),
                new BifrostLoggingConfiguration { LogSql = true, SlowQueryThresholdMs = 0 });
            await verbose.OnQueryPhaseAsync(BifrostQL.Core.Modules.QueryPhase.AfterExecute, context);

            var messages = log.GetSnapshot();
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

        private static (BifrostLoggingModule Module, FakeLogCollector Log) CreateModule()
        {
            var logger = new FakeLogger<BifrostLoggingModule>();
            return (new BifrostLoggingModule(logger, new BifrostLoggingConfiguration()), logger.Collector);
        }
    }
}