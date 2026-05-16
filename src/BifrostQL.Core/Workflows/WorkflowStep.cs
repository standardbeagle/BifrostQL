using System.Text.Json;

namespace BifrostQL.Core.Workflows;

/// <summary>
/// Ordered workflow step. <see cref="Type"/> discriminates the shape of
/// <see cref="Payload"/>; validation lives in <see cref="WorkflowConfigCollector"/>.
/// </summary>
public sealed record WorkflowStep
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public JsonElement? Payload { get; init; }

    public string? Output { get; init; }
}
