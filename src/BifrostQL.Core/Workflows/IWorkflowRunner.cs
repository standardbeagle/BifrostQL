using System.Text.Json;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Workflows;

public interface IWorkflowRunner
{
    Task<WorkflowRunResult> RunAsync(
        string name,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext);

    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext);
}

public sealed record WorkflowRunResult(
    bool Succeeded,
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<WorkflowStepTrace> Trace,
    WorkflowRunError? Error = null);

public sealed record WorkflowStepTrace(
    string StepName,
    string StepType,
    bool Succeeded,
    object? Output = null,
    string? Error = null);

public sealed record WorkflowRunError(
    string Code,
    string Message,
    string? StepName = null);

public sealed class WorkflowRunner : IWorkflowRunner
{
    private const string InputError = "Invalid workflow input.";
    private const string StepError = "Workflow step failed.";

    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _workflows;
    private readonly IWorkflowDataExecutor _executor;

    public WorkflowRunner(
        IReadOnlyDictionary<string, WorkflowDefinition> workflows,
        IWorkflowDataExecutor executor)
    {
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public Task<WorkflowRunResult> RunAsync(
        string name,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name is required.", nameof(name));

        if (!_workflows.TryGetValue(name, out var workflow))
        {
            return Task.FromResult(Failed(
                "workflow_not_found",
                "Workflow was not found.",
                Array.Empty<WorkflowStepTrace>()));
        }

        return RunAsync(workflow, inputs, userContext);
    }

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        IDictionary<string, object?> inputs,
        IDictionary<string, object?> userContext)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(userContext);

        var inputError = ValidateInputs(workflow, inputs);
        if (inputError is not null)
            return Failed("invalid_input", inputError, Array.Empty<WorkflowStepTrace>());

        var trace = new List<WorkflowStepTrace>();
        var context = new WorkflowExecutionContext(inputs, userContext, trace);

        foreach (var step in workflow.Steps)
        {
            var result = await RunStepAsync(step, context);
            trace.Add(result.Trace);
            if (!result.Succeeded)
            {
                return Failed(
                    result.ErrorCode ?? "step_failed",
                    result.Trace.Error ?? StepError,
                    trace,
                    step.Name);
            }

            if (!string.IsNullOrWhiteSpace(step.Output))
                context.NamedOutputs[step.Output] = result.Output;
        }

        return new WorkflowRunResult(true, new Dictionary<string, object?>(context.NamedOutputs), trace);
    }

    private async Task<StepRunResult> RunStepAsync(
        WorkflowStep step,
        WorkflowExecutionContext context)
    {
        try
        {
            var output = step.Type.ToLowerInvariant() switch
            {
                "query" => await RunQueryAsync(step, context),
                "mutation" => await RunMutationAsync(step, context),
                "transition" => await RunTransitionAsync(step, context),
                "policy-check" => RunPolicyCheck(step),
                "audit" => await RunAuditAsync(step, context),
                "branch" => await RunBranchAsync(step, context),
                "parallel" => await RunParallelAsync(step, context),
                _ => throw new WorkflowStepException("step_failed", StepError),
            };

            return StepRunResult.Success(new WorkflowStepTrace(step.Name, step.Type, true, output), output);
        }
        catch (WorkflowStepException ex)
        {
            return StepRunResult.Failure(
                new WorkflowStepTrace(step.Name, step.Type, false, Error: ex.Message),
                ex.Code);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            // Classify server-side against the full exception chain: the raw
            // driver message (e.g. "UNIQUE constraint failed") now lives on the
            // InnerException because the client-facing message is sanitized.
            var code = MentionsUniqueViolation(ex) ? "conflict" : "step_failed";
            return StepRunResult.Failure(
                new WorkflowStepTrace(step.Name, step.Type, false, Error: StepError),
                code);
        }
    }

    private static bool MentionsUniqueViolation(Exception? ex)
    {
        // The raw driver text (e.g. "UNIQUE constraint failed") is sanitized out
        // of client-facing messages, so match either the safe conflict message
        // that survives the pipeline or any raw driver fingerprint still present
        // on the exception chain.
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.Contains(BifrostExecutionError.ConflictMessage, StringComparison.Ordinal))
                return true;
        }
        return BifrostExecutionError.IsUniqueViolation(ex);
    }

    private async Task<object?> RunQueryAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var table = RequiredString(payload, "table");
        var id = ResolveValue(RequiredProperty(payload, "id"), context)
            ?? throw new WorkflowStepException("step_failed", StepError);
        var row = await _executor.QuerySingleAsync(table, id, context.UserContext);
        if (row is null
            && payload.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.True)
            throw new WorkflowStepException("not_found", "Workflow row was not found.");
        return row;
    }

    private async Task<object?> RunMutationAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var table = RequiredString(payload, "table");
        var action = OptionalString(payload, "action") ?? "update";
        var values = ResolveObject(RequiredProperty(payload, "values"), context);

        if (string.Equals(action, "insert", StringComparison.OrdinalIgnoreCase))
            await _executor.InsertAsync(table, values, context.UserContext);
        else if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
            await _executor.UpdateAsync(table, values, context.UserContext);
        else
            throw new WorkflowStepException("step_failed", StepError);

        return values;
    }

    private async Task<object?> RunTransitionAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var table = RequiredString(payload, "table");
        var id = ResolveValue(RequiredProperty(payload, "id"), context)
            ?? throw new WorkflowStepException("step_failed", StepError);
        var stateColumn = RequiredString(payload, "stateColumn");
        var from = RequiredString(payload, "from");
        var to = RequiredString(payload, "to");

        var row = await _executor.QuerySingleAsync(table, id, context.UserContext);
        if (row is null
            || !row.TryGetValue(stateColumn, out var current)
            || !string.Equals(current?.ToString(), from, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowStepException("not_found", "Workflow row was not found.");
        }

        var values = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase)
        {
            [stateColumn] = to,
        };
        await _executor.UpdateAsync(table, values, context.UserContext);
        return values;
    }

    private static object RunPolicyCheck(WorkflowStep step)
    {
        var payload = RequirePayload(step);
        if (payload.TryGetProperty("allowed", out var allowed)
            && allowed.ValueKind == JsonValueKind.False)
        {
            throw new WorkflowStepException("step_failed", StepError);
        }

        _ = RequiredString(payload, "table");
        _ = RequiredString(payload, "action");
        return true;
    }

    private async Task<object?> RunAuditAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var values = new Dictionary<string, object?>
        {
            ["action"] = ResolveValue(RequiredProperty(payload, "action"), context),
            ["entity_type"] = ResolveValue(RequiredProperty(payload, "entityType"), context),
            ["entity_id"] = ResolveValue(RequiredProperty(payload, "entityId"), context)?.ToString(),
        };

        if (payload.TryGetProperty("summary", out var summary))
            values["summary"] = ResolveValue(summary, context);
        if (payload.TryGetProperty("tenantId", out var tenantId))
            values["tenant_id"] = ResolveValue(tenantId, context);
        if (payload.TryGetProperty("actorUserId", out var actorUserId))
            values["actor_user_id"] = ResolveValue(actorUserId, context);
        if (payload.TryGetProperty("createdAt", out var createdAt))
            values["created_at"] = ResolveValue(createdAt, context);

        await _executor.InsertAsync("audit_log", values, context.UserContext);
        return values;
    }

    private async Task<object?> RunBranchAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var branch = EvaluateCondition(RequiredProperty(payload, "condition"), context)
            ? "then"
            : "else";

        if (!payload.TryGetProperty(branch, out var steps) || steps.ValueKind != JsonValueKind.Array)
            return null;

        return await RunInlineStepsAsync(steps, context);
    }

    private async Task<object?> RunParallelAsync(WorkflowStep step, WorkflowExecutionContext context)
    {
        var payload = RequirePayload(step);
        var steps = RequiredProperty(payload, "steps");
        if (steps.ValueKind != JsonValueKind.Array)
            throw new WorkflowStepException("step_failed", StepError);

        var inlineSteps = DeserializeSteps(steps);
        var tasks = inlineSteps.Select(s =>
        {
            var isolated = new WorkflowExecutionContext(context.Inputs, context.UserContext, context.Trace);
            foreach (var (key, value) in context.NamedOutputs)
                isolated.NamedOutputs[key] = value;
            return RunStepAsync(s, isolated);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        if (results.Any(r => !r.Succeeded))
            throw new WorkflowStepException("step_failed", StepError);

        return results.Select(r => r.Output).ToArray();
    }

    private async Task<object?> RunInlineStepsAsync(JsonElement steps, WorkflowExecutionContext context)
    {
        object? last = null;
        foreach (var inlineStep in DeserializeSteps(steps))
        {
            var result = await RunStepAsync(inlineStep, context);
            context.Trace.Add(result.Trace);
            if (!result.Succeeded)
                throw new WorkflowStepException(result.ErrorCode ?? "step_failed", result.Trace.Error ?? StepError);

            last = result.Output;
            if (!string.IsNullOrWhiteSpace(inlineStep.Output))
                context.NamedOutputs[inlineStep.Output] = result.Output;
        }

        return last;
    }

    private static IReadOnlyList<WorkflowStep> DeserializeSteps(JsonElement steps)
        => JsonSerializer.Deserialize<IReadOnlyList<WorkflowStep>>(
               steps.GetRawText(),
               WorkflowJson.Options)
           ?? Array.Empty<WorkflowStep>();

    private static JsonElement RequirePayload(WorkflowStep step)
    {
        if (step.Payload is null || step.Payload.Value.ValueKind != JsonValueKind.Object)
            throw new WorkflowStepException("step_failed", StepError);
        return step.Payload.Value;
    }

    private static JsonElement RequiredProperty(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new WorkflowStepException("step_failed", StepError);
        }

        return value;
    }

    private static string RequiredString(JsonElement payload, string name)
        => ResolveLiteralString(RequiredProperty(payload, name))
           ?? throw new WorkflowStepException("step_failed", StepError);

    private static string? OptionalString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) ? ResolveLiteralString(value) : null;

    private static string? ResolveLiteralString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

    private static Dictionary<string, object?> ResolveObject(
        JsonElement value,
        WorkflowExecutionContext context)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new WorkflowStepException("step_failed", StepError);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            result[property.Name] = ResolveValue(property.Value, context);
        return result;
    }

    private static object? ResolveValue(JsonElement value, WorkflowExecutionContext context)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ResolveTemplate(value.GetString()!, context),
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => ResolveObject(value, context),
            JsonValueKind.Array => value.EnumerateArray().Select(v => ResolveValue(v, context)).ToArray(),
            _ => value.ToString(),
        };
    }

    private static object? ResolveTemplate(string value, WorkflowExecutionContext context)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("{{", StringComparison.Ordinal) || !trimmed.EndsWith("}}", StringComparison.Ordinal))
            return value;

        var path = trimmed[2..^2].Trim();
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A malformed template (e.g. "{{ inputs }}") or an unknown root (a typo like
        // "{{ input.x }}") must fail rather than be written verbatim into a mutation
        // or state-transition payload — a silent "{{ ... }}" literal in the database
        // is a data-integrity hazard, not a valid default.
        if (parts.Length < 2)
            throw new BifrostExecutionError(
                $"Malformed workflow template '{value}'; expected '{{{{ inputs.<name> }}}}' or '{{{{ steps.<step>.<name> }}}}'.");

        if (string.Equals(parts[0], "inputs", StringComparison.OrdinalIgnoreCase))
            return ResolvePath(context.Inputs, parts.Skip(1));

        if (string.Equals(parts[0], "steps", StringComparison.OrdinalIgnoreCase))
        {
            // A missing step output is legitimately null (the step may not have run,
            // e.g. a skipped conditional branch); only the structural cases throw.
            if (!context.NamedOutputs.TryGetValue(parts[1], out var stepOutput))
                return null;
            return ResolvePath(stepOutput, parts.Skip(2));
        }

        throw new BifrostExecutionError(
            $"Unknown workflow template root '{parts[0]}' in '{value}'; expected 'inputs' or 'steps'.");
    }

    private static object? ResolvePath(object? value, IEnumerable<string> path)
    {
        var current = value;
        foreach (var segment in path)
        {
            if (current is IDictionary<string, object?> dictionary)
            {
                current = dictionary.TryGetValue(segment, out var next) ? next : null;
                continue;
            }

            return null;
        }

        return current;
    }

    private static bool EvaluateCondition(JsonElement condition, WorkflowExecutionContext context)
    {
        if (condition.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return condition.GetBoolean();

        if (condition.ValueKind != JsonValueKind.Object
            || !condition.TryGetProperty("equals", out var equals)
            || equals.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowStepException("step_failed", StepError);
        }

        var left = ResolveValue(RequiredProperty(equals, "left"), context);
        var right = ResolveValue(RequiredProperty(equals, "right"), context);
        return Equals(left?.ToString(), right?.ToString());
    }

    private static string? ValidateInputs(
        WorkflowDefinition workflow,
        IDictionary<string, object?> inputs)
    {
        if (workflow.Inputs is null
            || workflow.Inputs.Value.ValueKind != JsonValueKind.Object
            || !workflow.Inputs.Value.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in required.EnumerateArray())
        {
            var key = item.GetString();
            if (string.IsNullOrWhiteSpace(key)
                || !inputs.TryGetValue(key, out var value)
                || value is null)
            {
                return InputError;
            }
        }

        return null;
    }

    private static WorkflowRunResult Failed(
        string code,
        string message,
        IReadOnlyList<WorkflowStepTrace> trace,
        string? stepName = null)
        => new(false, new Dictionary<string, object?>(), trace, new WorkflowRunError(code, message, stepName));

    private sealed class WorkflowExecutionContext
    {
        public WorkflowExecutionContext(
            IDictionary<string, object?> inputs,
            IDictionary<string, object?> userContext,
            List<WorkflowStepTrace> trace)
        {
            Inputs = inputs;
            UserContext = userContext;
            Trace = trace;
        }

        public IDictionary<string, object?> Inputs { get; }
        public IDictionary<string, object?> UserContext { get; }
        public Dictionary<string, object?> NamedOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WorkflowStepTrace> Trace { get; }
    }

    private sealed record StepRunResult(bool Succeeded, WorkflowStepTrace Trace, object? Output, string? ErrorCode = null)
    {
        public static StepRunResult Success(WorkflowStepTrace trace, object? output)
            => new(true, trace, output);

        public static StepRunResult Failure(WorkflowStepTrace trace)
            => new(false, trace, null);

        public static StepRunResult Failure(WorkflowStepTrace trace, string errorCode)
            => new(false, trace, null, errorCode);
    }

    private sealed class WorkflowStepException : Exception
    {
        public WorkflowStepException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
