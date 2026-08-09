using System.Text;

namespace BifrostQL.Core.SavedObjects;

/// <summary>
/// File-backed saved-object store for the desktop shell: one JSON file per object at
/// <c>&lt;baseDir&gt;/saved-objects/&lt;owner&gt;/&lt;type&gt;/&lt;id&gt;.json</c>. Writes are
/// atomic (temp file + rename) so a crash never leaves a half-written object, and every path
/// segment is sanitized so an <c>id</c>/<c>type</c> can never escape the base directory.
/// A process-wide lock serializes writes so the read-modify-version-check-write cycle
/// is not interleaved within one process.
///
/// <para>The owner segment is VALIDATED by <see cref="SavedObjectOwner.Require"/>, not sanitized
/// like the id: sanitizing it would let two distinct owners collapse onto the same directory and
/// silently share a partition, which is precisely the isolation failure the owner exists to
/// prevent. Because it is validated it is already a safe segment and is used verbatim.</para>
///
/// <para><b>Objects written before the owner dimension existed</b> live one level up, at
/// <c>&lt;baseDir&gt;/saved-objects/&lt;type&gt;/&lt;id&gt;.json</c>. They are UNREACHABLE through
/// this store — never listed, read, overwritten or deleted — and are left untouched on disk rather
/// than adopted or removed. See the type-level remarks on why.</para>
/// </summary>
public sealed class FileSavedObjectStore : ISavedObjectStore
{
    private const string RootFolder = "saved-objects";
    private readonly string _baseDir;
    private readonly int _maxDefinitionBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileSavedObjectStore(string baseDir, int maxDefinitionBytes = SavedObjectJson.DefaultMaxDefinitionBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
        _baseDir = baseDir;
        _maxDefinitionBytes = maxDefinitionBytes;
    }

    private string TypeDir(string owner, SavedObjectType type)
        => Path.Combine(_baseDir, RootFolder, SavedObjectOwner.Require(owner), type.ToString().ToLowerInvariant());

    private string PathFor(string owner, SavedObjectType type, string id)
        => Path.Combine(TypeDir(owner, type), SanitizeSegment(id) + ".json");

    /// <summary>
    /// Reduces an id to a safe single path segment: every invalid filename character
    /// plus the path separators and drive/scheme punctuation becomes '_', so a crafted
    /// id (<c>../../etc</c>) cannot traverse out of the type directory.
    /// </summary>
    private static string SanitizeSegment(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var extra = new[] { '/', '\\', ':', '<', '>', '|', '"', '*', '?' };
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || Array.IndexOf(extra, ch) >= 0 ? '_' : ch);
        var result = sb.ToString();
        return result.Length == 0 ? "_" : result;
    }

    public async Task<IReadOnlyList<SavedObject>> ListAsync(string owner, SavedObjectType? type, CancellationToken cancellationToken = default)
    {
        var types = type.HasValue ? new[] { type.Value } : Enum.GetValues<SavedObjectType>();
        var result = new List<SavedObject>();
        foreach (var t in types)
        {
            var dir = TypeDir(owner, t);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var obj = await ReadFileAsync(file, cancellationToken);
                if (obj != null)
                    result.Add(obj);
            }
        }
        return result;
    }

    public async Task<SavedObject?> GetAsync(string owner, SavedObjectType type, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return await ReadFileAsync(PathFor(owner, type, id), cancellationToken);
    }

    public async Task<SavedObject> PutAsync(string owner, SavedObject obj, CancellationToken cancellationToken = default)
    {
        SavedObjectJson.Validate(obj, _maxDefinitionBytes);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(owner, obj.Type, obj.Id);
            var existing = await ReadFileAsync(path, cancellationToken);
            var nextVersion = ResolveNextVersion(obj, existing);

            var toWrite = obj with { Version = nextVersion };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(SavedObjectJson.Serialize(toWrite));

            // Atomic write: a partial file under the temp name never becomes the object.
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, cancellationToken);
            File.Move(tmp, path, overwrite: true);
            return toWrite;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task DeleteAsync(string owner, SavedObjectType type, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = PathFor(owner, type, id);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The version to persist: a create (no existing object) requires an incoming
    /// version of 0 and yields 1; an update requires the incoming version to equal the
    /// stored version and yields version+1. Any mismatch is a lost-update conflict.
    /// </summary>
    private static int ResolveNextVersion(SavedObject incoming, SavedObject? existing)
    {
        if (existing == null)
        {
            if (incoming.Version != 0)
                throw new SavedObjectVersionConflictException(incoming.Type, incoming.Id, incoming.Version, 0);
            return 1;
        }
        if (incoming.Version != existing.Version)
            throw new SavedObjectVersionConflictException(incoming.Type, incoming.Id, incoming.Version, existing.Version);
        return existing.Version + 1;
    }

    private static async Task<SavedObject?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        // A corrupt file returns null rather than failing a whole list read.
        return SavedObjectJson.TryDeserializeStored(text);
    }
}
