using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Mcp.Test.Eval
{
    /// <summary>
    /// The result of one tool invocation as the eval harness sees it: whether the tool
    /// returned an error, the textual response an agent would read, and the parsed
    /// structured payload (when the tool produced one) for golden assertions.
    /// </summary>
    public sealed record McpEvalCallResult(bool IsError, string ResponseText, JsonElement? Structured);

    /// <summary>
    /// The single seam the harness depends on: invoke a tool by name with arguments and
    /// return its <see cref="McpEvalCallResult"/>. The MCP integration test supplies an
    /// implementation that goes over the real SDK client/server wire; slice 7 of the
    /// declarative-tools epic re-runs the SAME harness by supplying its own invoker
    /// (its transport, its tool layer) — nothing else about the harness changes.
    /// </summary>
    public delegate Task<McpEvalCallResult> McpToolInvoker(string tool, IReadOnlyDictionary<string, object?> args);

    /// <summary>One scripted tool call plus an optional golden assertion over its structured payload.</summary>
    public sealed class McpEvalStep
    {
        public required string Tool { get; init; }
        public required IReadOnlyDictionary<string, object?> Args { get; init; }

        /// <summary>Whether this step is expected to return a tool error (default: success).</summary>
        public bool ExpectError { get; init; }

        /// <summary>
        /// Golden check: returns null on success or a failure reason. Runs against the
        /// step's structured payload. Skipped for steps that expect an error.
        /// </summary>
        public Func<JsonElement, string?>? Assert { get; init; }
    }

    /// <summary>A realistic multi-step task expressed as a scripted sequence of tool calls.</summary>
    public sealed class McpEvalScenario
    {
        public required string Name { get; init; }
        public required IReadOnlyList<McpEvalStep> Steps { get; init; }
    }

    /// <summary>Per-scenario recorded metrics.</summary>
    public sealed class McpEvalScenarioResult
    {
        public required string Name { get; init; }
        public required int Calls { get; init; }
        public required int Errors { get; init; }
        public required int ResponseChars { get; init; }
        public required int ResponseBytes { get; init; }
        public required bool Passed { get; init; }

        [JsonIgnore] public string? FailureReason { get; init; }
    }

    /// <summary>Aggregate metrics across every scenario.</summary>
    public sealed class McpEvalTotals
    {
        public required int Calls { get; init; }
        public required int Errors { get; init; }
        public required int ResponseChars { get; init; }
        public required int ResponseBytes { get; init; }
        public required double ErrorRate { get; init; }
    }

    /// <summary>The harness's report: per-scenario metrics plus totals. Serialized as the baseline artifact.</summary>
    public sealed class McpEvalReport
    {
        public required IReadOnlyList<McpEvalScenarioResult> Scenarios { get; init; }
        public required McpEvalTotals Totals { get; init; }

        [JsonIgnore] public bool AllPassed => Scenarios.All(s => s.Passed);

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);
    }

    /// <summary>
    /// A small, no-live-LLM eval harness for the BifrostQL MCP tool surface. It runs
    /// scripted tool-call sequences against a supplied <see cref="McpToolInvoker"/>,
    /// checks golden assertions, and records the response size (chars + UTF-8 bytes),
    /// call counts, and error rate — the metrics an agent's context budget and cost are
    /// driven by. It is deliberately decoupled from any transport: the same scenarios and
    /// metric definitions can be replayed against a different tool layer (declarative-tools
    /// slice 7) by passing a different invoker, so the baseline stays comparable.
    /// </summary>
    public sealed class McpEvalHarness
    {
        public async Task<McpEvalReport> RunAsync(McpToolInvoker invoker, IReadOnlyList<McpEvalScenario> scenarios)
        {
            ArgumentNullException.ThrowIfNull(invoker);
            ArgumentNullException.ThrowIfNull(scenarios);

            var results = new List<McpEvalScenarioResult>(scenarios.Count);
            foreach (var scenario in scenarios)
                results.Add(await RunScenarioAsync(invoker, scenario));

            var calls = results.Sum(r => r.Calls);
            var errors = results.Sum(r => r.Errors);
            var totals = new McpEvalTotals
            {
                Calls = calls,
                Errors = errors,
                ResponseChars = results.Sum(r => r.ResponseChars),
                ResponseBytes = results.Sum(r => r.ResponseBytes),
                ErrorRate = calls == 0 ? 0.0 : Math.Round((double)errors / calls, 4),
            };
            return new McpEvalReport { Scenarios = results, Totals = totals };
        }

        private static async Task<McpEvalScenarioResult> RunScenarioAsync(McpToolInvoker invoker, McpEvalScenario scenario)
        {
            int calls = 0, errors = 0, chars = 0, bytes = 0;
            bool passed = true;
            string? failure = null;

            foreach (var step in scenario.Steps)
            {
                var result = await invoker(step.Tool, step.Args);
                calls++;
                chars += result.ResponseText.Length;
                bytes += Encoding.UTF8.GetByteCount(result.ResponseText);
                if (result.IsError)
                    errors++;

                if (passed && (failure = CheckStep(step, result)) is not null)
                    passed = false;
            }

            return new McpEvalScenarioResult
            {
                Name = scenario.Name,
                Calls = calls,
                Errors = errors,
                ResponseChars = chars,
                ResponseBytes = bytes,
                Passed = passed,
                FailureReason = failure,
            };
        }

        private static string? CheckStep(McpEvalStep step, McpEvalCallResult result)
        {
            if (step.ExpectError)
                return result.IsError ? null : $"step '{step.Tool}' expected a tool error but succeeded";
            if (result.IsError)
                return $"step '{step.Tool}' failed: {result.ResponseText}";
            if (step.Assert is null)
                return null;
            if (result.Structured is not { } structured)
                return $"step '{step.Tool}' returned no structured payload to assert on";
            return step.Assert(structured);
        }
    }
}
