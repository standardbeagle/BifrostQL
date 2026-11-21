using System.Text.Json;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// How a run reacts when a step's executor call faults: cancellation stops the run, any other
/// fault becomes a step failure whose detail reaches the log, not only the fixed trace text.
/// </summary>
[Collection(ErrorSinkLoggerCollection.Name)]
public class WorkflowRunnerFaultTests
{
    private static WorkflowRunner Runner(IWorkflowDataExecutor executor, out WorkflowDefinition workflow)
    {
        workflow = new WorkflowDefinition
        {
            Name = "load-member",
            Trigger = new WorkflowTrigger { Type = "manual" },
            Steps = new[]
            {
                new WorkflowStep
                {
                    Name = "load",
                    Type = "query",
                    Payload = JsonDocument.Parse("""{ "table": "members", "id": 1 }""").RootElement.Clone(),
                },
            },
        };
        return new WorkflowRunner(new Dictionary<string, WorkflowDefinition> { [workflow.Name] = workflow }, executor);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_ThrowsInsteadOfReportingAStepFailure()
    {
        using var cts = new CancellationTokenSource();
        var executor = new FaultingExecutor(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new InvalidOperationException("unreachable");
        });
        var runner = Runner(executor, out var workflow);

        var run = () => runner.RunAsync(workflow, new Dictionary<string, object?>(), new Dictionary<string, object?>(), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        executor.SeenTokens.Should().ContainSingle().Which.Should().Be(cts.Token, "the run threads the caller's token to the executor");
    }

    [Fact]
    public async Task RunAsync_WhenAStepFaults_LogsTheFaultAndReportsStepFailed()
    {
        var runner = Runner(new FaultingExecutor(_ => new InvalidOperationException("driver said no")), out var workflow);

        WorkflowRunResult result;
        using var logger = new ErrorSinkCapture();
        result = await runner.RunAsync(workflow, new Dictionary<string, object?>(), new Dictionary<string, object?>());

        result.Error!.Code.Should().Be("step_failed");
        result.Trace.Single().Error.Should().NotContain("driver said no", "the trace keeps the fixed step text");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
            && e.Message.Contains("'load'") && e.Exception!.Message == "driver said no");
    }

    private sealed class FaultingExecutor(Func<CancellationToken, Exception> fault) : IWorkflowDataExecutor
    {
        public List<CancellationToken> SeenTokens { get; } = new();

        public Task<IDictionary<string, object?>?> QuerySingleAsync(
            string table, object id, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            SeenTokens.Add(cancellationToken);
            throw fault(cancellationToken);
        }

        public Task InsertAsync(string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateAsync(string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task TransitionAsync(WorkflowTransition transition, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
