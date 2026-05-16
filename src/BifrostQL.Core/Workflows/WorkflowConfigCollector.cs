using BifrostQL.Core.AppMetadata;

namespace BifrostQL.Core.Workflows;

/// <summary>
/// Extracts and validates workflow definitions from the app-metadata overlay.
/// </summary>
public static class WorkflowConfigCollector
{
    private const string ErrorMessage = "Invalid workflow metadata.";

    private static readonly HashSet<string> TriggerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "manual",
        "on-mutation",
        "on-state-transition",
        "scheduled",
    };

    private static readonly HashSet<string> StepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "query",
        "mutation",
        "transition",
        "policy-check",
        "audit",
        "branch",
        "parallel",
    };

    public static IReadOnlyDictionary<string, WorkflowDefinition> FromOverlay(AppMetadataModel metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var workflows = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);
        if (metadata.Workflows is null)
            return workflows;

        foreach (var workflow in metadata.Workflows)
        {
            ValidateWorkflow(workflow);
            if (!workflows.TryAdd(workflow.Name, workflow))
                throw new InvalidOperationException(ErrorMessage);
        }

        return workflows;
    }

    public static IReadOnlyDictionary<string, WorkflowDefinition> FromJson(string json)
    {
        var metadata = WorkflowJson.DeserializeOverlay(json);
        return FromOverlay(metadata);
    }

    private static void ValidateWorkflow(WorkflowDefinition workflow)
    {
        if (workflow is null)
            throw new InvalidOperationException(ErrorMessage);

        if (string.IsNullOrWhiteSpace(workflow.Name))
            throw new InvalidOperationException(ErrorMessage);

        if (workflow.Trigger is null
            || string.IsNullOrWhiteSpace(workflow.Trigger.Type)
            || !TriggerTypes.Contains(workflow.Trigger.Type))
        {
            throw new InvalidOperationException(ErrorMessage);
        }

        if (workflow.Steps is null || workflow.Steps.Count == 0)
            throw new InvalidOperationException(ErrorMessage);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in workflow.Steps)
        {
            ValidateStep(step);
            if (!names.Add(step.Name))
                throw new InvalidOperationException(ErrorMessage);
        }
    }

    private static void ValidateStep(WorkflowStep step)
    {
        if (step is null)
            throw new InvalidOperationException(ErrorMessage);

        if (string.IsNullOrWhiteSpace(step.Name)
            || string.IsNullOrWhiteSpace(step.Type)
            || !StepTypes.Contains(step.Type))
        {
            throw new InvalidOperationException(ErrorMessage);
        }

        var requiredFields = RequiredPayloadFields(step.Type);
        if (requiredFields.Length == 0)
            return;

        if (step.Payload is null || step.Payload.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidOperationException(ErrorMessage);

        foreach (var field in requiredFields)
        {
            if (!step.Payload.Value.TryGetProperty(field, out var value)
                || value.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            {
                throw new InvalidOperationException(ErrorMessage);
            }
        }
    }

    private static string[] RequiredPayloadFields(string type) => type.ToLowerInvariant() switch
    {
        "query" => new[] { "table" },
        "mutation" => new[] { "table", "values" },
        "transition" => new[] { "table", "from", "to" },
        "policy-check" => new[] { "table", "action" },
        "audit" => new[] { "action", "entityType", "entityId" },
        "branch" => new[] { "condition" },
        "parallel" => new[] { "steps" },
        _ => Array.Empty<string>(),
    };
}
