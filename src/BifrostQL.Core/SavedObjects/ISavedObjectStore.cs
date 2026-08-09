namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// Persistence for user-authored <see cref="SavedObject"/>s. Two implementations
/// sit behind this seam: a file-backed store (desktop, JSON files under the profile
/// config dir) and a DB-backed store (hosted, a dedicated table, opt-in via config).
/// The HTTP surface and the optimistic-concurrency contract are identical across both.
///
/// <para>Every operation is scoped to an OWNER — a token from <see cref="SavedObjectOwner"/>,
/// derived from the caller's projected identity and never from anything the client supplies. The
/// owner is a leading parameter rather than an ambient/implicit filter so an implementation
/// physically cannot answer an unscoped query: there is no method here that spans owners. Object
/// identity is therefore (owner, type, id), and the SAME (type, id) held by two callers is two
/// distinct objects. A caller reading, overwriting or deleting another owner's id sees exactly
/// what it would see for an id nobody holds — no existence oracle.</para>
/// </summary>
public interface ISavedObjectStore
{
    /// <summary>
    /// <paramref name="owner"/>'s objects, optionally filtered to one <paramref name="type"/>.
    /// Never spans owners. Newest writes are not ordered — callers sort.
    /// </summary>
    Task<IReadOnlyList<SavedObject>> ListAsync(string owner, SavedObjectType? type, CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="owner"/>'s object of <paramref name="type"/> with <paramref name="id"/>,
    /// or null if absent. Another owner's object is absent, not forbidden — the two answers must
    /// be indistinguishable.
    /// </summary>
    Task<SavedObject?> GetAsync(string owner, SavedObjectType type, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates <paramref name="obj"/> within <paramref name="owner"/>'s partition. A
    /// create (no existing row for this owner) requires <see cref="SavedObject.Version"/> 0 and
    /// persists version 1; an update requires the incoming version to equal the stored version and
    /// persists version+1. Throws <see cref="SavedObjectVersionConflictException"/> on a stale
    /// write. Returns the persisted object with its new version.
    ///
    /// <para>The version check is per owner. A write to an id ANOTHER owner holds is a create in
    /// this owner's own partition — never a conflict, which would leak that the id is taken, and
    /// never an overwrite.</para>
    /// </summary>
    Task<SavedObject> PutAsync(string owner, SavedObject obj, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes <paramref name="owner"/>'s object of <paramref name="type"/> with
    /// <paramref name="id"/>. No-op if absent — including when another owner holds that id.
    /// </summary>
    Task DeleteAsync(string owner, SavedObjectType type, string id, CancellationToken cancellationToken = default);
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
