namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// Persistence for user-authored <see cref="SavedObject"/>s. Two implementations
/// sit behind this seam: a file-backed store (desktop, JSON files under the profile
/// config dir) and a DB-backed store (hosted, a dedicated table, opt-in via config).
/// The HTTP surface and the optimistic-concurrency contract are identical across both.
/// </summary>
public interface ISavedObjectStore
{
    /// <summary>All objects, optionally filtered to one <paramref name="type"/>. Newest writes are not ordered — callers sort.</summary>
    Task<IReadOnlyList<SavedObject>> ListAsync(SavedObjectType? type, CancellationToken cancellationToken = default);

    /// <summary>The object of <paramref name="type"/> with <paramref name="id"/>, or null if absent.</summary>
    Task<SavedObject?> GetAsync(SavedObjectType type, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates <paramref name="obj"/>. A create (no existing row) requires
    /// <see cref="SavedObject.Version"/> 0 and persists version 1; an update requires
    /// the incoming version to equal the stored version and persists version+1.
    /// Throws <see cref="SavedObjectVersionConflictException"/> on a stale write.
    /// Returns the persisted object with its new version.
    /// </summary>
    Task<SavedObject> PutAsync(SavedObject obj, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object of <paramref name="type"/> with <paramref name="id"/>. No-op if absent.</summary>
    Task DeleteAsync(SavedObjectType type, string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a <see cref="ISavedObjectStore.PutAsync"/> carries a version that no
/// longer matches the stored object — a lost-update guard. The HTTP layer maps this
/// to 409 Conflict.
/// </summary>
public sealed class SavedObjectVersionConflictException : Exception
{
    public SavedObjectVersionConflictException(SavedObjectType type, string id, int expected, int actual)
        : base($"Saved object '{type}/{id}' was modified concurrently: write carried version {expected} but the stored version is {actual}. Reload and retry.")
    {
        Type = type;
        Id = id;
        ExpectedVersion = expected;
        ActualVersion = actual;
    }

    public SavedObjectType Type { get; }
    public string Id { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }
}
