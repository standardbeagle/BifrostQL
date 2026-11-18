using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// Identity-filtered <c>ListBuckets</c> and <c>ListObjectsV2</c> over the tables
    /// explicitly configured with file-storage columns.
    ///
    /// <para><b>Buckets are configured mappings, never inferred.</b> A bucket is a
    /// table that (a) declares at least one file-storage column, (b) has a legal S3
    /// bucket name, and (c) is READABLE by the caller, with every key column and at least
    /// one file column readable, under the SAME <see cref="PolicyEvaluator"/> gate the query
    /// path enforces, projected through <see cref="SchemaReadVisibility"/>, fail-closed on
    /// any evaluation fault (see .claude/rules/protocol-adapter-security.md invariant 4 and
    /// the pgwire catalog precedent <c>PgCatalogVisibility</c>). A table with no file
    /// column, a filesystem root, or a table the caller cannot read is never listed —
    /// so bucket enumeration is not an information-disclosure side channel.</para>
    ///
    /// <para><b>Objects resolve through the authorized read seam.</b> Rows come from
    /// <see cref="IQueryIntentExecutor"/>, so tenant isolation, soft-delete, row-scope
    /// policy, and column read guards all apply unconditionally — the lister builds no
    /// tenant/policy predicate of its own; the only predicate it constructs is ordering
    /// and it reads only the file-column pointer and primary key.</para>
    ///
    /// <para><b>Only readable columns are projected.</b> The column set comes from
    /// <see cref="SchemaReadVisibility.ProjectTable(IDbTable, IDictionary{string, object?})"/>,
    /// the evaluator the read pipeline enforces (invariant 4). A read-denied file column is
    /// left out of the scan, so its objects are simply not listed; projecting it would make the
    /// policy transformer refuse the whole read. A bucket whose key column is read-denied cannot
    /// address any object, so it is hidden from <c>ListBuckets</c> and answers
    /// <c>NoSuchBucket</c> on <c>ListObjectsV2</c>, the same as a bucket that does not exist.
    /// The same holds when every file column is denied.</para>
    ///
    /// <para><b>Pagination is stable and bounded.</b> Objects are ordered by object key
    /// (S3's ascending UTF-8 order; our keys are percent-encoded ASCII, so ordinal
    /// string order is that byte order), prefix/delimiter grouping matches S3, and the
    /// page is capped by <c>max-keys</c>. The resume position travels in an opaque,
    /// integrity-protected <see cref="S3ContinuationToken"/> bound to the bucket,
    /// prefix, delimiter, page size, and caller identity, so a tampered or cross-bucket
    /// token fails closed. It expires <see cref="S3Options.ContinuationTokenTtl"/> after it
    /// was issued.</para>
    ///
    /// <para><b>Bound:</b> a page is computed by materializing the bucket's scoped,
    /// prefix-filtered candidate set and slicing it — bounded by
    /// <see cref="S3Options.MaxListMaterialize"/>. A bucket with more addressable
    /// objects than that cap fails fast with a curated error rather than silently
    /// truncating a listing (which would lie about completeness). Each page re-reads that
    /// candidate set, so a full walk reads about N * ceil(N / max-keys) rows.</para>
    ///
    /// <para><b>Why not a primary-key seek.</b> S3 lists in UTF-8 key order and clients
    /// (<c>aws s3 sync</c>, rclone) merge listings on that assumption. Object keys are
    /// <c>column/pk</c> strings, whose order is not the database's PK order: integer keys
    /// sort "10" before "9", and GUID and collation order differ too. A seek on the PK
    /// would return pages out of key order, which a sync with <c>--delete</c> can act on
    /// wrongly. A seek needs either a listing-order contract change (column, then PK
    /// order) or an ORDER BY on a dialect-computed key expression; both are open.</para>
    /// </summary>
    public sealed class S3Listing
    {
        // A stable creation date for synthesized buckets: a table has none, and S3
        // clients do not rely on this value for correctness. Fixed rather than
        // "now" so the listing is deterministic.
        private static readonly DateTime BucketCreationDate = DateTime.UnixEpoch;

        private readonly IQueryIntentExecutor _reads;
        private readonly FileStorageService _storage;
        private readonly S3Options _options;
        private readonly ILogger? _logger;
        private readonly byte[] _tokenSecret;
        private readonly TimeProvider _clock;

        /// <param name="clock">Stamps and ages continuation tokens; null uses <see cref="TimeProvider.System"/>.</param>
        public S3Listing(
            IQueryIntentExecutor reads,
            S3Options options,
            FileStorageService? storage = null,
            ILogger<S3Listing>? logger = null,
            TimeProvider? clock = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _storage = storage ?? new FileStorageService();
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
            if (options.ContinuationTokenTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(options), options.ContinuationTokenTtl, "S3Options.ContinuationTokenTtl must be positive.");

            // A configured secret keeps continuation tokens valid across restarts and
            // across a horizontally-scaled fleet; without one we generate a per-instance
            // random key — tokens then survive only within this process's lifetime,
            // which is a documented trade-off, not a silent one.
            if (!string.IsNullOrEmpty(options.ContinuationTokenSecret))
            {
                _tokenSecret = Encoding.UTF8.GetBytes(options.ContinuationTokenSecret);
            }
            else
            {
                _tokenSecret = RandomNumberGenerator.GetBytes(32);
                _logger?.LogWarning(
                    "No S3 ContinuationTokenSecret configured; using a per-instance random key. " +
                    "In-flight continuation tokens will not survive a restart or resolve on another instance.");
            }
        }

        /// <summary>
        /// Lists the buckets visible to <paramref name="userContext"/>: every legally
        /// named, file-configured, policy-readable table. Emptiness does not hide a
        /// bucket (an empty bucket is still a bucket).
        /// </summary>
        public async Task<IReadOnlyList<S3BucketInfo>> ListBucketsAsync(
            IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userContext);

            var model = await _reads.GetModelAsync(_options.Endpoint);

            var buckets = new List<S3BucketInfo>();
            foreach (var table in model.Tables)
            {
                string bucketName;
                try
                {
                    bucketName = S3ObjectKeyMap.BucketNameFor(table);
                }
                catch (InvalidOperationException)
                {
                    // A table whose lowercased name is not a legal bucket name is simply
                    // not addressable over S3 — skip it rather than rewrite the name.
                    continue;
                }

                if (ReadableFileColumns(table, model, userContext) is null)
                    continue;

                buckets.Add(new S3BucketInfo(bucketName, BucketCreationDate));
            }

            buckets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return buckets;
        }

        /// <summary>
        /// Lists a page of objects in <paramref name="bucket"/> under the caller's
        /// identity. Throws <see cref="S3ProtocolException.NoSuchBucket"/> for an
        /// unknown, illegal, non-file, or not-visible bucket — the last three
        /// indistinguishable from "does not exist", so the listing is not a
        /// bucket-existence oracle.
        /// </summary>
        public async Task<S3ListObjectsPage> ListObjectsV2Async(
            string bucket,
            string? prefix,
            string? delimiter,
            int? maxKeys,
            string? continuationToken,
            string? startAfter,
            IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userContext);

            var model = await _reads.GetModelAsync(_options.Endpoint);

            IDbTable table;
            try
            {
                table = S3ObjectKeyMap.ResolveBucket(model, bucket);
            }
            catch (InvalidOperationException)
            {
                throw S3ProtocolException.NoSuchBucket();
            }

            // Fail-closed authorization on the bucket itself, identical to the object
            // read path's gate. A table the caller cannot read, or that is not a
            // file-configured bucket, or whose key or every file column is read-denied, is
            // reported as absent — never AccessDenied, which would confirm it exists.
            var fileColumns = ReadableFileColumns(table, model, userContext)
                ?? throw S3ProtocolException.NoSuchBucket();

            var effectivePrefix = prefix ?? string.Empty;
            var effectiveMaxKeys = ClampMaxKeys(maxKeys);

            var binding = new S3ListBinding(
                bucket, effectivePrefix, delimiter ?? string.Empty, effectiveMaxKeys, IdentityFingerprint(userContext));

            // A continuation token, when present, is the authoritative resume position
            // and overrides start-after (S3 semantics). It is validated against THIS
            // request's binding, so a token minted for another bucket/prefix/identity is
            // rejected before any query runs.
            string? resumeAfter;
            if (!string.IsNullOrEmpty(continuationToken))
                resumeAfter = S3ContinuationToken.Decode(
                    continuationToken, binding, _tokenSecret, _clock.GetUtcNow(), _options.ContinuationTokenTtl);
            else
                resumeAfter = string.IsNullOrEmpty(startAfter) ? null : startAfter;

            var allObjects = await MaterializeAsync(model, table, fileColumns, effectivePrefix, userContext, cancellationToken);

            return BuildPage(
                bucket, allObjects, effectivePrefix, delimiter, effectiveMaxKeys,
                resumeAfter, continuationToken, startAfter, binding);
        }

        // ---- materialization ------------------------------------------------------

        /// <summary>
        /// Reads every addressable object in the bucket through the authorized read
        /// seam: the primary key plus each file-storage column, ordered by primary key
        /// for a deterministic scan. Rows whose file column is null hold no object and
        /// are skipped. Bounded by <see cref="S3Options.MaxListMaterialize"/>, narrowed by
        /// the model's <c>max-query-rows</c> ceiling through <see cref="RowProbeWindow"/>.
        /// </summary>
        private async Task<List<S3ObjectInfo>> MaterializeAsync(
            IDbModel model, IDbTable table, IReadOnlyList<ColumnDto> fileColumns, string prefix,
            IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                // A keyless table has no addressable rows; treat it as an empty bucket
                // rather than throwing (it survived the file-column check by config).
                return new List<S3ObjectInfo>();

            // One past the cap, so we can detect (and fail fast on) a bucket that exceeds
            // what this slice can page in memory. The window comes from the same clamp SQL
            // generation applies: a hand-rolled MaxListMaterialize + 1 is cut to
            // max-query-rows, the count never exceeds the cap, and a partial bucket lists
            // as complete.
            var window = RowProbeWindow.For(model, _options.MaxListMaterialize);
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Sort = keyColumns.Select(c => $"{c.GraphQlName}_asc").ToList(),
                Limit = window.Fetch,
            };
            foreach (var keyColumn in keyColumns)
                query.ScalarColumns.Add(new GqlObjectColumn(keyColumn.DbName, keyColumn.GraphQlName));
            foreach (var fileColumn in fileColumns)
                query.ScalarColumns.Add(new GqlObjectColumn(fileColumn.DbName, fileColumn.GraphQlName));

            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = _options.Endpoint,
            }, cancellationToken);

            if (window.HasMore(result.Rows.Count))
                throw S3ProtocolException.InvalidArgument(
                    "This bucket has more objects than the endpoint is configured to list in a single scan.");

            var objects = new List<S3ObjectInfo>();
            foreach (var row in result.Rows)
            {
                var primaryKey = new object?[keyColumns.Count];
                for (var i = 0; i < keyColumns.Count; i++)
                    row.TryGetValue(keyColumns[i].GraphQlName, out primaryKey[i]);

                foreach (var fileColumn in fileColumns)
                {
                    if (!row.TryGetValue(fileColumn.GraphQlName, out var pointerValue))
                        continue;
                    var pointer = pointerValue?.ToString();
                    if (string.IsNullOrWhiteSpace(pointer))
                        continue;

                    var metadata = FileMetadata.FromJson(pointer);
                    if (metadata is null)
                        continue; // corrupt/unparseable pointer: not a listable object

                    var key = S3ObjectKeyMap.KeyFor(fileColumn, primaryKey);
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                        continue; // prefix filter applied before sort to bound the set

                    objects.Add(new S3ObjectInfo(key, metadata.UploadedAt, metadata.ETag, metadata.Size));
                }
            }

            objects.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return objects;
        }

        // ---- prefix/delimiter/page assembly ---------------------------------------

        /// <summary>
        /// Slices the sorted candidate set into one S3 page: applies delimiter roll-up
        /// into CommonPrefixes, resumes strictly after <paramref name="resumeAfter"/>,
        /// and caps the page at <paramref name="maxKeys"/> (keys and common prefixes
        /// counted together, as S3 does). The resume comparison uses each item's
        /// REPRESENTATIVE sort key — the common-prefix string for a rolled-up key —
        /// so a page boundary that lands on a CommonPrefix skips that whole group on
        /// resume instead of re-emitting it.
        /// </summary>
        private S3ListObjectsPage BuildPage(
            string bucket, List<S3ObjectInfo> allObjects, string prefix, string? delimiter,
            int maxKeys, string? resumeAfter, string? echoContinuationToken, string? echoStartAfter,
            S3ListBinding binding)
        {
            var hasDelimiter = !string.IsNullOrEmpty(delimiter);
            var contents = new List<S3ObjectInfo>();
            var commonPrefixes = new List<string>();
            var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;
            string? lastSortKey = null;
            var emitted = 0;

            foreach (var obj in allObjects)
            {
                // allObjects is already prefix-filtered in MaterializeAsync.
                string sortKey;
                var isPrefix = false;
                string? commonPrefix = null;

                if (hasDelimiter)
                {
                    var remainder = obj.Key[prefix.Length..];
                    var idx = remainder.IndexOf(delimiter!, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        commonPrefix = prefix + remainder[..(idx + delimiter!.Length)];
                        sortKey = commonPrefix;
                        isPrefix = true;
                    }
                    else
                    {
                        sortKey = obj.Key;
                    }
                }
                else
                {
                    sortKey = obj.Key;
                }

                // Resume strictly after the cursor position, on the representative key.
                if (resumeAfter != null && string.CompareOrdinal(sortKey, resumeAfter) <= 0)
                    continue;

                // A key rolling into a prefix already emitted in THIS page is not a new
                // item — skip it before it can count toward the page or trip truncation.
                if (isPrefix && seenPrefixes.Contains(commonPrefix!))
                    continue;

                if (emitted == maxKeys)
                {
                    // There is at least one more item beyond this full page.
                    truncated = true;
                    break;
                }

                if (isPrefix)
                {
                    seenPrefixes.Add(commonPrefix!);
                    commonPrefixes.Add(commonPrefix!);
                }
                else
                {
                    contents.Add(obj);
                }

                emitted++;
                lastSortKey = sortKey;
            }

            string? nextToken = null;
            if (truncated)
            {
                // lastSortKey is null only for the degenerate max-keys=0 poll; resume
                // from the same position so the client makes no phantom progress.
                var position = lastSortKey ?? resumeAfter ?? string.Empty;
                nextToken = S3ContinuationToken.Issue(position, _clock.GetUtcNow(), binding, _tokenSecret);
            }

            return new S3ListObjectsPage(
                Bucket: bucket,
                Prefix: prefix,
                Delimiter: string.IsNullOrEmpty(delimiter) ? null : delimiter,
                MaxKeys: maxKeys,
                IsTruncated: truncated,
                Objects: contents,
                CommonPrefixes: commonPrefixes,
                ContinuationToken: string.IsNullOrEmpty(echoContinuationToken) ? null : echoContinuationToken,
                NextContinuationToken: nextToken,
                StartAfter: string.IsNullOrEmpty(echoStartAfter) ? null : echoStartAfter);
        }

        // ---- policy / configuration helpers ---------------------------------------

        private int ClampMaxKeys(int? requested)
        {
            var value = requested ?? _options.DefaultMaxKeys;
            if (value < 0)
                throw S3ProtocolException.InvalidArgument("max-keys must not be negative.");
            return Math.Min(value, _options.MaxKeysLimit);
        }

        /// <summary>
        /// The file columns of <paramref name="table"/> the caller may read, or null when the
        /// table is not a bucket for this caller: the table is unreadable, a key column is
        /// read-denied (no object key can be built), or no readable file column remains. The
        /// projection is <see cref="SchemaReadVisibility"/>'s, fail-closed on any evaluation
        /// fault, so the listing never asks the read pipeline for a column it would refuse.
        /// </summary>
        private IReadOnlyList<ColumnDto>? ReadableFileColumns(
            IDbTable table, IDbModel model, IDictionary<string, object?> userContext)
        {
            var visible = SchemaReadVisibility.ProjectTable(table, userContext);
            if (visible is null || !visible.HasColumns(table.KeyColumns))
                return null;

            var result = new List<ColumnDto>();
            foreach (var column in visible.Columns)
                if (_storage.IsFileStorageColumn(table, column, model))
                    result.Add(column);
            return result.Count == 0 ? null : result;
        }

        /// <summary>
        /// A stable fingerprint of the caller's identity-visible query shape, folded
        /// into every continuation token so a token minted for one caller is rejected
        /// when replayed by another. Built from the scalar and string-sequence entries
        /// of the user context (user id, roles, tenant, org, permissions, …) — the same
        /// values the read pipeline scopes on — hashed so no identity plaintext ever
        /// reaches the wire. Non-scalar/opaque context entries are excluded so the
        /// fingerprint is deterministic across requests by the same principal.
        /// </summary>
        private static string IdentityFingerprint(IDictionary<string, object?> userContext)
        {
            return ProtocolIdentityFingerprint.Of(userContext);
        }
    }
}
