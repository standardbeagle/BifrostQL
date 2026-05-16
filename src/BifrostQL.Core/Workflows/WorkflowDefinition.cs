using System.Text.Json;

namespace BifrostQL.Core.Workflows;

/// <summary>
/// Pure data contract for a declarative workflow loaded from app metadata.
/// </summary>
public sealed record WorkflowDefinition
{
    public required string Name { get; init; }

    public required WorkflowTrigger Trigger { get; init; }

    public JsonElement? Inputs { get; init; }

    public JsonElement? Outputs { get; init; }

    public IReadOnlyList<WorkflowStep> Steps { get; init; } = Array.Empty<WorkflowStep>();
}

public sealed record WorkflowTrigger
{
    public required string Type { get; init; }

    public JsonElement? Payload { get; init; }
}
