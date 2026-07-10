using System.Text.Json;

namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// The kind of user-authored object a <see cref="SavedObject"/> holds. Serialized
/// camelCase over the wire (e.g. <c>"query"</c>); an unrecognized value is rejected
/// by the store rather than silently coerced.
/// </summary>
public enum SavedObjectType
{
    Query,
    Form,
    Report,
    Dashboard,
}

/// <summary>
/// One persisted user-authored object: a query/form/report/dashboard the client
/// designed. <see cref="Definition"/> is the opaque, type-specific payload (a JSON
/// document the server does not interpret). <see cref="Version"/> is an optimistic
/// concurrency token — a write must carry the version it last read, and the store
/// increments it on success and rejects a stale write.
/// </summary>
public sealed record SavedObject
{
    public required string Id { get; init; }
    public required SavedObjectType Type { get; init; }
    public required string Name { get; init; }

    /// <summary>Optional folder path for client-side organization; null = root.</summary>
    public string? Folder { get; init; }

    /// <summary>Opaque, type-specific definition payload. Not interpreted server-side.</summary>
    public required JsonElement Definition { get; init; }

    /// <summary>
    /// Optimistic concurrency token. A create carries 0 (no prior version); the store
    /// persists it as 1. An update carries the version last read; the store writes
    /// version+1 iff it matches the stored version, else raises a conflict.
    /// </summary>
    public int Version { get; init; }
}
