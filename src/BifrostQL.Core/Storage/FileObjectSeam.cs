using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Storage;

/// <summary>Host configuration for a <see cref="FileObjectSeam"/>.</summary>
public sealed class FileObjectSeamOptions
{
    /// <summary>
    /// The registered GraphQL endpoint whose cached model/connection the seam
    /// reads and writes against. Null selects the single registered endpoint.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Whether <see cref="FileObjectSeam.PutAsync"/>/<see cref="FileObjectSeam.DeleteAsync"/>
    /// are permitted. Defaults to OFF: a write surface over file objects is a
    /// dangerous opt-in capability, so it is fail-closed by construction and
    /// enabling it logs a startup warning (see
    /// .claude/rules/protocol-adapter-security.md invariant 7).
    /// </summary>
    public bool EnableWrites { get; init; }
}

/// <summary>
/// The programmatic file-object seam: the ONLY way an adapter reaches a file
/// object, and the reason it cannot reach one it is not allowed to see.
///
/// <para><b>Reads</b> go through <see cref="IQueryIntentExecutor"/>, so every
/// filter transformer (tenant isolation, soft delete, row-scope policy, column
/// read guards) applies unconditionally. <b>Writes</b> go through
/// <see cref="IMutationIntentExecutor"/> — hence the full
/// <c>TableMutationPipeline</c> (policy, validation, audit, concurrency,
/// encryption-on-write, CDC/history hooks). The seam supplies ONLY the positional
/// primary key decoded from the object key plus the caller's
/// <c>UserContext</c>; it builds NO WHERE clause of its own, so the pipeline
/// narrows scope from the identity and an out-of-scope key matches zero rows
/// structurally rather than because this class remembered to filter.</para>
///
/// <para><b>A stream cannot be obtained without an authorized row.</b>
/// <see cref="GetContentAsync"/> accepts only a <see cref="ResolvedFileObject"/>,
/// which has no public constructor: it can be minted only by
/// <see cref="ResolveAsync"/>, which performs the identity-gated read first. So
/// no adapter-facing (public) API can reach a stream or a storage target without
/// a row the caller has been proven able to see — the check cannot be forgotten
/// because there is no signature that omits it. The honest bound on this
/// guarantee: construction is <c>internal</c>, so first-party code inside
/// BifrostQL.Core and its <c>InternalsVisibleTo</c> list (which includes
/// BifrostQL.Server) could fabricate one. That code could equally call the
/// storage provider directly; the seam constrains the adapter's API surface, it
/// is not a sandbox against Bifrost's own internals.</para>
///
/// <para><b>An S3 object is a column value, not a row.</b> Deleting an object
/// therefore routes an <c>Update</c> intent setting the file column to NULL — the
/// same modelling <see cref="FileDeleteResolver"/> uses — never a <c>Delete</c>
/// intent, which would destroy (or soft-delete) the whole row and every other
/// column on it. Whether that row-clearing update is itself soft-deleted, audited
/// or vetoed remains the pipeline's decision, not this seam's.</para>
///
/// <para><b>Failure ordering is deliberate</b> — see <see cref="PutAsync"/> and
/// <see cref="DeleteAsync"/>.</para>
/// </summary>
public sealed class FileObjectSeam
{
    private readonly IQueryIntentExecutor _reads;
    private readonly IMutationIntentExecutor _writes;
    private readonly FileStorageService _storage;
    private readonly FileObjectSeamOptions _options;

    public FileObjectSeam(
        IQueryIntentExecutor reads,
        IMutationIntentExecutor writes,
        FileStorageService? storage = null,
        FileObjectSeamOptions? options = null,
        ILogger<FileObjectSeam>? logger = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _storage = storage ?? new FileStorageService();
        _options = options ?? new FileObjectSeamOptions();

        if (_options.EnableWrites)
            logger?.LogWarning(
                "FileObjectSeam writes are ENABLED: callers can overwrite and clear file columns through the " +
                "file-object surface. This is a posture change from the fail-closed default.");
    }

    /// <summary>
    /// A file object the caller has been proven able to see. The constructor is
    /// private and the factory internal, so no adapter-facing API can mint one
    /// without going through <see cref="ResolveAsync"/>. The storage
    /// path/target is deliberately NOT exposed on the public surface — an adapter
    /// gets the S3-facing contract and nothing it could use to reach storage on
    /// its own.
    /// </summary>
    public sealed class ResolvedFileObject
    {
        private ResolvedFileObject(
            string bucket, string key, IDbModel model, IDbTable table, ColumnDto column,
            IReadOnlyList<object?> primaryKey, FileMetadata metadata)
        {
            Bucket = bucket;
            Key = key;
            Model = model;
            Table = table;
            Column = column;
            PrimaryKey = primaryKey;
            Metadata = metadata;
        }

        public string Bucket { get; }
        public string Key { get; }

        /// <summary>MIME type recorded at upload; null when the writer supplied none.</summary>
        public string? ContentType => Metadata.ContentType;

        /// <summary>Byte length recorded at upload.</summary>
        public long ContentLength => Metadata.Size;

        /// <summary>
        /// The object's entity tag (MD5 hex of the content, persisted at upload).
        /// Null for rows written before the ETag contract existed; the S3 codec
        /// decides how to present that, since fabricating one here would mean
        /// downloading the object on every HEAD.
        /// </summary>
        public string? ETag => Metadata.ETag;

        public DateTime LastModified => Metadata.UploadedAt;

        public string? OriginalName => Metadata.OriginalName;

        public IReadOnlyDictionary<string, string> CustomMetadata =>
            Metadata.CustomMetadata ?? EmptyMetadata;

        private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
            new Dictionary<string, string>();

        // These carry the storage target, so they stay off the public surface;
        // the seam reaches them through the internal Unpack() below. (C# gives the
        // ENCLOSING type no access to a nested type's private members, so the
        // accessor has to be internal — assembly-scoped, not adapter-reachable.)
        private IDbModel Model { get; }
        private IDbTable Table { get; }
        private ColumnDto Column { get; }
        private IReadOnlyList<object?> PrimaryKey { get; }
        private FileMetadata Metadata { get; }

        internal static ResolvedFileObject Create(
            string bucket, string key, IDbModel model, IDbTable table, ColumnDto column,
            IReadOnlyList<object?> primaryKey, FileMetadata metadata) =>
            new(bucket, key, model, table, column, primaryKey, metadata);

        internal (IDbModel Model, IDbTable Table, ColumnDto Column, IReadOnlyList<object?> PrimaryKey, FileMetadata Metadata)
            Unpack() => (Model, Table, Column, PrimaryKey, Metadata);
    }

    /// <summary>
    /// Resolves an object address to the file object it names, under the caller's
    /// identity. Returns null when the row is not visible to the caller, does not
    /// exist, or holds no file — all indistinguishable by design, so the seam is
    /// not a row-existence oracle. Throws only on addressing faults (unknown
    /// bucket, malformed key, non-file column) and on a corrupt pointer.
    /// </summary>
    public async Task<ResolvedFileObject?> ResolveAsync(
        string bucket, string key, IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        var located = await LocateAsync(bucket, key, userContext, cancellationToken);
        if (!located.RowVisible || located.Metadata is null)
            return null;

        return ResolvedFileObject.Create(
            bucket, key, located.Model, located.Table, located.Column, located.PrimaryKey, located.Metadata);
    }

    /// <summary>
    /// Reads the object's bytes. Takes a <see cref="ResolvedFileObject"/> — which
    /// only <see cref="ResolveAsync"/> can mint — so there is no code path to a
    /// stream that skips the identity-gated row read.
    /// </summary>
    public async Task<byte[]> GetContentAsync(
        ResolvedFileObject fileObject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileObject);

        var (model, table, column, _, metadata) = fileObject.Unpack();
        return await _storage.DownloadFileAsync(table, column, model, metadata, cancellationToken);
    }

    /// <summary>
    /// Writes an object's content and repoints its row at it.
    ///
    /// <para>Ordering: the row is resolved (proving it exists and is writable by
    /// this caller) BEFORE any storage access; the blob is then uploaded; the
    /// pointer is updated last, through the mutation pipeline, which may still
    /// veto. A veto or failure at that final step triggers a compensating delete
    /// of the just-uploaded blob, because an orphaned blob the caller was told
    /// nothing about is the one outcome with no owner. If the compensating delete
    /// ALSO fails, both failures are surfaced together — never swallowed — since
    /// the residue then needs an operator.</para>
    ///
    /// <para>Because the key is deterministic, re-putting the same object
    /// overwrites in place: no orphan accumulates across writes. (Objects written
    /// before this seam existed carry a random key from
    /// <see cref="FileMetadata.GenerateFileKey"/>; re-putting one writes the new
    /// deterministic key and leaves the old blob behind. Reclaiming those is a
    /// sweeper's job, not this write path's.)</para>
    /// </summary>
    public async Task<ResolvedFileObject> PutAsync(
        string bucket, string key, byte[] content, string? contentType,
        IReadOnlyDictionary<string, string>? customMetadata,
        IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        // FIRST check, before arity/key parsing or model lookup: a disabled
        // surface builds zero intent and cannot be probed for behaviour.
        AssertWritesEnabled();
        ArgumentNullException.ThrowIfNull(content);

        var located = await LocateAsync(bucket, key, userContext, cancellationToken);
        if (!located.RowVisible)
            throw new BifrostExecutionError($"Object '{bucket}/{key}' does not exist or is not accessible.");

        var metadata = await _storage.UploadFileAsync(
            located.Table, located.Column, located.Model,
            recordId: string.Empty,
            content: content,
            originalFileName: null,
            contentType: contentType,
            fileKey: key,
            customMetadata: customMetadata,
            cancellationToken: cancellationToken);

        try
        {
            await UpdatePointerAsync(located, metadata.ToJson(), cancellationToken);
        }
        catch (Exception mutationFailure)
        {
            await CompensateAsync(located, metadata, mutationFailure, cancellationToken);
            throw;
        }

        return ResolvedFileObject.Create(
            bucket, key, located.Model, located.Table, located.Column, located.PrimaryKey, metadata);
    }

    /// <summary>
    /// Removes an object: clears the row's file pointer, then deletes the blob.
    /// Returns false when there was no object to delete (S3 delete is idempotent).
    ///
    /// <para>Ordering: the pointer is cleared FIRST, through the mutation
    /// pipeline. This is the opposite of <see cref="FileDeleteResolver"/>'s
    /// blob-first order and the divergence is deliberate: the pipeline is the
    /// authorization gate and it may veto, so deleting the blob before it runs
    /// would destroy content that the veto was supposed to protect —
    /// unrecoverable. Clearing the pointer first bounds the worst case to a blob
    /// that no row references any more (invisible to every Bifrost surface,
    /// reclaimable by a sweeper) instead of a row advertising content that is
    /// already gone. A failing blob delete after a cleared pointer still throws:
    /// the residue is real and an operator should hear about it.</para>
    /// </summary>
    public async Task<bool> DeleteAsync(
        string bucket, string key, IDictionary<string, object?> userContext,
        CancellationToken cancellationToken = default)
    {
        AssertWritesEnabled();

        var located = await LocateAsync(bucket, key, userContext, cancellationToken);
        if (!located.RowVisible)
            throw new BifrostExecutionError($"Object '{bucket}/{key}' does not exist or is not accessible.");

        if (located.Metadata is null)
            return false;

        await UpdatePointerAsync(located, null, cancellationToken);
        await _storage.DeleteFileAsync(located.Table, located.Column, located.Model, located.Metadata, cancellationToken);
        return true;
    }

    // ---- internals -----------------------------------------------------------

    private void AssertWritesEnabled()
    {
        if (!_options.EnableWrites)
            throw new InvalidOperationException(
                "File-object writes are not enabled. Set FileObjectSeamOptions.EnableWrites to permit " +
                "put/delete through the file-object surface.");
    }

    private readonly record struct LocatedObject(
        bool RowVisible, IDbModel Model, IDbTable Table, ColumnDto Column,
        IReadOnlyList<object?> PrimaryKey, FileMetadata? Metadata,
        IDictionary<string, object?> UserContext);

    /// <summary>
    /// The single identity gate every operation passes through: map the address
    /// to (table, column, key), then read the pointer via the read-intent seam so
    /// the filter pipeline decides visibility. A row the caller cannot see comes
    /// back as zero rows — the seam never sees it and never asks storage about it.
    /// </summary>
    private async Task<LocatedObject> LocateAsync(
        string bucket, string key, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
    {
        var model = await _reads.GetModelAsync(_options.Endpoint);
        var table = S3ObjectKeyMap.ResolveBucket(model, bucket);
        var address = S3ObjectKeyMap.ParseKey(table, key);

        // Only columns configured for file storage are addressable as objects;
        // otherwise the seam would turn every column into a download endpoint.
        if (!_storage.IsFileStorageColumn(table, address.Column, model))
            throw new InvalidOperationException(
                $"Column '{address.Column.ColumnName}' of table '{table.DbName}' is not configured for file storage.");

        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            Filter = PrimaryKeyFilter(table, address),
            Limit = 1,
        };
        query.ScalarColumns.Add(new GqlObjectColumn(address.Column.DbName, address.Column.GraphQlName));

        var result = await _reads.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = userContext,
            Endpoint = _options.Endpoint,
        }, cancellationToken);

        if (result.Rows.Count == 0)
            return new LocatedObject(false, model, table, address.Column, address.PrimaryKey, null, userContext);

        var pointer = result.Rows[0][address.Column.GraphQlName]?.ToString();
        if (string.IsNullOrWhiteSpace(pointer))
            return new LocatedObject(true, model, table, address.Column, address.PrimaryKey, null, userContext);

        var metadata = FileMetadata.FromJson(pointer)
            ?? throw new BifrostExecutionError(
                $"File metadata for '{table.DbName}.{address.Column.ColumnName}' is not parseable; " +
                "the stored file reference is corrupt.");

        return new LocatedObject(true, model, table, address.Column, address.PrimaryKey, metadata, userContext);
    }

    /// <summary>
    /// The row's own identity, and nothing else. This is the ONLY predicate the
    /// seam constructs, and it addresses exactly one row by its full (possibly
    /// composite) key. Tenant/policy/soft-delete scoping is added by the
    /// transformer pipeline on top of it — never re-implemented here.
    /// </summary>
    private static TableFilter PrimaryKeyFilter(IDbTable table, FileObjectAddress address)
    {
        var keyColumns = table.KeyColumns.ToList();
        var clauses = keyColumns
            .Select((column, i) => TableFilterFactory.Equals(table.DbName, column.ColumnName, address.PrimaryKey[i]))
            .ToList();

        return clauses.Count == 1
            ? clauses[0]
            : new TableFilter { FilterType = FilterType.And, And = clauses };
    }

    /// <summary>
    /// Repoints (or clears) the row's file column through the mutation pipeline.
    /// The seam passes the positional primary key — composite-key safe, never a
    /// first-column guess — and the caller's UserContext, and builds no predicate:
    /// the pipeline narrows the write from the identity.
    /// </summary>
    private async Task UpdatePointerAsync(LocatedObject located, string? pointerJson, CancellationToken cancellationToken)
    {
        var result = await _writes.ExecuteAsync(new MutationIntent
        {
            Table = located.Table.DbName,
            Action = MutationIntentAction.Update,
            PrimaryKey = located.PrimaryKey,
            Data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [located.Column.ColumnName] = pointerJson,
            },
            UserContext = new Dictionary<string, object?>(located.UserContext),
            Endpoint = _options.Endpoint,
        }, cancellationToken);

        // A composite-key update returns affected rows; a single-key update
        // returns the key. Either way, zero affected means the pipeline scoped
        // the write away between the read and the write (row deleted, reassigned
        // or soft-deleted). Reporting that as success would strand a blob.
        if (result.Value is int affected && affected == 0)
            throw new BifrostExecutionError(
                $"Row of '{located.Table.DbName}' is no longer accessible; the file pointer was not updated.");
    }

    /// <summary>
    /// Rolls the just-uploaded blob back after the pointer write failed. A
    /// failure here is surfaced alongside the original one rather than swallowed:
    /// the object is now unreferenced storage residue that only an operator can
    /// reclaim.
    /// </summary>
    private async Task CompensateAsync(
        LocatedObject located, FileMetadata metadata, Exception mutationFailure, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteFileAsync(located.Table, located.Column, located.Model, metadata, cancellationToken);
        }
        catch (Exception rollbackFailure)
        {
            throw new BifrostExecutionError(
                $"Writing the file pointer for '{located.Table.DbName}.{located.Column.ColumnName}' failed, and " +
                $"rolling the uploaded object back failed too: an orphaned object remains at storage key " +
                $"'{metadata.FileKey}' and must be reclaimed manually.",
                new AggregateException(mutationFailure, rollbackFailure));
        }
    }
}
