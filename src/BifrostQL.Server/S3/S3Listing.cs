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
    /// tenant/policy predicate of its own; the only predicate it constructs is the
    /// object-key window, and it reads only the file-column pointer and primary key.</para>
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
    /// <para><b>Pagination is a keyset seek in S3 key order.</b> S3 lists in ascending
    /// UTF-8 key order and clients (<c>aws s3 sync</c>, rclone) merge listings on that
    /// assumption. Object keys are <c>column/pk</c> strings whose order is not the
    /// database's PK order (integer keys sort "10" before "9"), so the database orders by
    /// the key text itself: <see cref="ISqlDialect.ObjectKeyExpression"/> computes each
    /// row's key suffix in SQL, byte for byte as <see cref="S3ObjectKeyMap"/> renders it,
    /// under a binary collation. Keys are percent-encoded ASCII, so binary order is UTF-8
    /// order on every engine (SQL Server's UTF-16 <c>_BIN2</c> included). A seek reads the
    /// <c>max-keys + 1</c> rows after the resume position (<c>key &gt; @after</c>, the
    /// prefix as <c>key &gt;= @prefix AND key &lt; @successor</c>, all parameters); the extra
    /// row is the IsTruncated sentinel. Each file column is its own key range, visited in
    /// key order. Every returned key is checked in C# against the seek's order and bounds,
    /// and a disagreement fails the request rather than paging out of order.</para>
    ///
    /// <para><b>Delimiter roll-up runs in C#</b> over the ordered rows: once a
    /// CommonPrefix is emitted, the next seek starts past every key under it, so a
    /// rolled-up group costs one row, not its size. A page runs at most
    /// <see cref="MaxSeeksPerPage"/> seeks; a page that needs more (long runs of rows
    /// with no pointer, or many jumps) ends early as truncated with a token at the scan
    /// position, which S3 permits. The resume position travels in an opaque,
    /// integrity-protected <see cref="S3ContinuationToken"/> bound to the bucket, prefix,
    /// delimiter, page size, and caller identity, so a tampered or cross-bucket token
    /// fails closed. It expires <see cref="S3Options.ContinuationTokenTtl"/> after it was
    /// issued.</para>
    ///
    /// <para><b>Key types the database cannot order.</b> <see cref="S3KeyListingStrategy"/>
    /// decides per table, from the declared key types, whether the dialect renders every
    /// key column as the exact text <see cref="S3ObjectKeyMap.KeyFor"/> builds. When it does
    /// not (float keys on SQL Server, MySQL <c>FLOAT</c>, SQLite REAL-affinity
    /// columns, decimals with a scale above 28), each page reads the bucket's raw key values
    /// with the same file-column filter and projection as a seek, formats every key in C#,
    /// and sorts ordinally. That read is bounded in the query by
    /// <see cref="S3Options.MaxInMemoryListKeys"/> (one past it, through
    /// <see cref="RowProbeWindow"/>); a bucket over the bound fails with a curated
    /// <c>InvalidArgument</c>, never a truncated listing. Tokens and page assembly are the
    /// seek path's.</para>
    ///
    /// <para>Rows whose file column is NULL are filtered in SQL on both paths; a non-null
    /// but unreadable pointer is skipped here, and together with CommonPrefix jumps that is
    /// why a seek page may need more than one seek.</para>
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
            if (options.MaxInMemoryListKeys < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(options), options.MaxInMemoryListKeys, "S3Options.MaxInMemoryListKeys must be at least 1.");

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

            var request = new PageRequest(
                bucket, effectivePrefix, string.IsNullOrEmpty(delimiter) ? null : delimiter, effectiveMaxKeys,
                resumeAfter, continuationToken, startAfter, binding);
            return await ListPageAsync(model, table, fileColumns, request, userContext, cancellationToken);
        }

        // ---- keyset page --------------------------------------------------------------

        /// <summary>The most database seeks one page may run before it ends early as truncated.</summary>
        internal const int MaxSeeksPerPage = 32;

        private sealed record PageRequest(
            string Bucket, string Prefix, string? Delimiter, int MaxKeys, string? ResumeAfter,
            string? EchoContinuationToken, string? EchoStartAfter, S3ListBinding Binding);

        /// <summary>
        /// Builds one S3 page with the strategy <see cref="S3KeyListingStrategy"/> chose for
        /// the table: a keyset seek ordered in SQL, or an in-memory sort of keys formatted in C#.
        /// </summary>
        private async Task<S3ListObjectsPage> ListPageAsync(
            IDbModel model, IDbTable table, IReadOnlyList<ColumnDto> fileColumns, PageRequest request,
            IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();

            // A keyless table has no addressable rows: an empty bucket. A prefix outside
            // ASCII matches nothing, because every key is percent-encoded ASCII.
            if (keyColumns.Count == 0 || request.Prefix.Any(c => c > '\u007F'))
                return Page(request, new PageAssembly(request, _logger), nextToken: null);

            var strategy = S3KeyListingStrategy.For(model, table);
            if (strategy.SortsInSql)
                return await SeekPageAsync(model, table, keyColumns, fileColumns, request, userContext, cancellationToken);

            _logger?.LogDebug(
                "S3 listing of table {Table} sorts keys in memory: {Reason}", table.DbName, strategy.InMemoryReason);
            return await InMemoryPageAsync(model, table, keyColumns, fileColumns, request, userContext, cancellationToken);
        }

        /// <summary>
        /// Builds one S3 page by seeking through the bucket in object-key order. Each file
        /// column is a key range of its own (<c>{column}/…</c>), visited in key order; within
        /// one, every seek reads the next <c>max-keys + 1</c> rows after the scan position,
        /// ordered and bounded in SQL on the dialect-computed key text. Delimiter roll-up
        /// runs in <see cref="PageAssembly"/>: once a CommonPrefix is emitted (or lies at or
        /// before the resume position), the scan jumps past every key under it.
        /// </summary>
        private async Task<S3ListObjectsPage> SeekPageAsync(
            IDbModel model, IDbTable table, IReadOnlyList<ColumnDto> keyColumns, IReadOnlyList<ColumnDto> fileColumns,
            PageRequest request, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var keyReadTypes = await KeyReadTypesAsync(table, keyColumns, userContext, cancellationToken);

            // Full-key bounds. The prefix is a fixed range; the scan position moves.
            var prefixFrom = request.Prefix.Length > 0 ? request.Prefix : null;
            var prefixBefore = request.Prefix.Length > 0 ? Successor(request.Prefix) : null;
            var page = new PageAssembly(request, _logger);

            var window = RowProbeWindow.For(model, request.MaxKeys);
            var seeks = 0;

            foreach (var column in fileColumns.OrderBy(S3ObjectKeyMap.ColumnPrefix, StringComparer.Ordinal))
            {
                var columnPrefix = S3ObjectKeyMap.ColumnPrefix(column);
                while (true)
                {
                    var range = SuffixRange(columnPrefix, page.Scan, prefixFrom, prefixBefore, keyReadTypes);
                    if (range == null)
                        break; // the scan has nothing left in this column's key range

                    if (seeks == MaxSeeksPerPage)
                    {
                        // Bounded work per request: hand back what this page found, resuming
                        // where the scan stopped. S3 allows a short (even empty) truncated page.
                        return Page(request, page, Issue(page.ScanStop, request));
                    }
                    seeks++;

                    var query = FileRowsQuery(table, keyColumns, column, window.Fetch);
                    query.ObjectKeyRange = range;
                    var rows = await ReadAsync(query, userContext, cancellationToken);
                    string? previousKey = null;
                    foreach (var row in rows)
                    {
                        var key = KeyOf(row, keyColumns, column);
                        AssertKeyOrder(key, previousKey, columnPrefix, range);
                        previousKey = key;

                        row.TryGetValue(column.GraphQlName, out var pointer);
                        var sentinelAt = page.Offer(key, pointer);
                        if (sentinelAt != null)
                            return Page(request, page, Issue(sentinelAt, request));
                    }

                    if (rows.Count < window.Fetch)
                        break; // the column's key range holds nothing past the last row read
                }
            }

            return Page(request, page, nextToken: null);
        }

        /// <summary>
        /// Builds one S3 page for a bucket whose key order the database cannot compute: reads
        /// every file row (the same <see cref="FileRowsQuery"/> as a seek, without the key
        /// window), formats each key with <see cref="S3ObjectKeyMap.KeyFor"/>, sorts ordinally,
        /// and feeds the sorted keys to the same <see cref="PageAssembly"/>. The read is capped
        /// in the query at <see cref="S3Options.MaxInMemoryListKeys"/> rows across all file
        /// columns, plus one sentinel row; a bucket past the cap (or past the model's
        /// <c>max-query-rows</c>, which <see cref="RowProbeWindow"/> folds in) is refused, never
        /// listed short.
        /// </summary>
        private async Task<S3ListObjectsPage> InMemoryPageAsync(
            IDbModel model, IDbTable table, IReadOnlyList<ColumnDto> keyColumns, IReadOnlyList<ColumnDto> fileColumns,
            PageRequest request, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var candidates = new List<(string Key, object? Pointer)>();
            var rowsRead = 0;
            foreach (var column in fileColumns)
            {
                var window = RowProbeWindow.For(model, _options.MaxInMemoryListKeys - rowsRead);
                var query = FileRowsQuery(table, keyColumns, column, window.Fetch);
                query.Sort = keyColumns.Select(c => $"{c.GraphQlName}_asc").ToList();
                var rows = await ReadAsync(query, userContext, cancellationToken);
                if (window.HasMore(rows.Count))
                    throw S3ProtocolException.InvalidArgument(
                        "This bucket has more objects than the endpoint is configured to list in a single scan.");
                rowsRead += rows.Count;

                foreach (var row in rows)
                {
                    var key = KeyOf(row, keyColumns, column);
                    if (!key.StartsWith(request.Prefix, StringComparison.Ordinal))
                        continue;
                    row.TryGetValue(column.GraphQlName, out var pointer);
                    candidates.Add((key, pointer));
                }
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var page = new PageAssembly(request, _logger);
            string? previousKey = null;
            foreach (var (key, pointer) in candidates)
            {
                // Two rows with one key would make the resume position ambiguous.
                if (previousKey != null && string.CompareOrdinal(key, previousKey) <= 0)
                    throw new InvalidOperationException("Two rows of the bucket format to the same S3 object key.");
                previousKey = key;

                var sentinelAt = page.Offer(key, pointer);
                if (sentinelAt != null)
                    return Page(request, page, Issue(sentinelAt, request));
            }

            return Page(request, page, nextToken: null);
        }

        /// <summary>
        /// One page's emitted keys and CommonPrefixes, fed keys in S3 key order by either
        /// strategy. Applies the scan position (keys already jumped past), the resume
        /// position, delimiter roll-up and the max-keys + 1 sentinel.
        /// </summary>
        private sealed class PageAssembly
        {
            private readonly PageRequest _request;
            private readonly ILogger? _logger;
            private int _emitted;
            private string? _lastEmitted;

            public PageAssembly(PageRequest request, ILogger? logger)
            {
                _request = request;
                _logger = logger;
                Scan = new ScanPosition(request.ResumeAfter, null);
            }

            public List<S3ObjectInfo> Contents { get; } = new();
            public List<string> CommonPrefixes { get; } = new();

            /// <summary>Where the scan stands; keys it does not admit are skipped.</summary>
            public ScanPosition Scan { get; private set; }

            /// <summary>The resume position for a page that stops before its sentinel.</summary>
            public string ScanStop => Scan.Through ?? _request.ResumeAfter ?? string.Empty;

            /// <summary>
            /// Takes the next key in S3 key order and its file pointer. Returns the resume
            /// position when <paramref name="key"/> is an item beyond a full page (the
            /// max-keys + 1 sentinel), otherwise null.
            /// </summary>
            public string? Offer(string key, object? pointerValue)
            {
                // Rows the scan already jumped past (keys under an emitted CommonPrefix)
                // are skipped without moving it.
                if (!Scan.Admits(key))
                    return null;

                var pointer = pointerValue?.ToString();
                var metadata = string.IsNullOrWhiteSpace(pointer) ? null : FileMetadata.FromJson(pointer);
                if (metadata is null)
                {
                    // A corrupt pointer is no object (GetObject answers NoSuchKey for it too);
                    // the scan still moves past it. Logged, so the object does not vanish
                    // from listings unnoticed.
                    if (!string.IsNullOrWhiteSpace(pointer))
                        _logger?.LogWarning(
                            "S3 listing of bucket {Bucket} skipped key {Key}: its stored file pointer is not parseable file metadata",
                            _request.Bucket, key);
                    Scan = new ScanPosition(key, null);
                    return null;
                }

                var commonPrefix = CommonPrefixOf(key, _request.Prefix, _request.Delimiter);
                var sortKey = commonPrefix ?? key;

                // Resume strictly after the cursor, on the representative key (a CommonPrefix
                // for a rolled-up key), so a page boundary on a CommonPrefix skips that whole
                // group instead of re-emitting it.
                var alreadyListed = _request.ResumeAfter != null
                    && string.CompareOrdinal(sortKey, _request.ResumeAfter) <= 0;
                if (!alreadyListed)
                {
                    if (_emitted == _request.MaxKeys)
                        return _lastEmitted ?? _request.ResumeAfter ?? string.Empty;

                    if (commonPrefix == null)
                        Contents.Add(new S3ObjectInfo(key, metadata.UploadedAt, metadata.ETag, metadata.Size));
                    else
                        CommonPrefixes.Add(commonPrefix);
                    _emitted++;
                    _lastEmitted = sortKey;
                }

                Scan = commonPrefix == null
                    ? new ScanPosition(key, null)
                    : new ScanPosition(commonPrefix, Successor(commonPrefix));
                return null;
            }
        }

        /// <summary>
        /// Where the scan stands, on full object keys: everything at or before
        /// <see cref="Through"/> has been scanned, and when <see cref="From"/> is set (a
        /// jump past a CommonPrefix) every key below it has too.
        /// </summary>
        private readonly record struct ScanPosition(string? Through, string? From)
        {
            public bool Admits(string key) =>
                (Through == null || string.CompareOrdinal(key, Through) > 0)
                && (From == null || string.CompareOrdinal(key, From) >= 0);
        }

        /// <summary>
        /// The scan's bounds in <paramref name="columnPrefix"/>'s suffix space (keys there
        /// are <c>columnPrefix + suffix</c>), or null when none of that column's keys lie
        /// inside them. A full-key bound B relates to the column's keys one of three ways:
        /// B starts with the prefix (it becomes a bound on the suffix), B sorts below the
        /// prefix (every key is above it), or B sorts above it (every key is below it).
        /// </summary>
        private static ObjectKeyRange? SuffixRange(
            string columnPrefix, ScanPosition scan, string? prefixFrom, string? prefixBefore, IReadOnlyList<Type?> keyReadTypes)
        {
            string? after = null, from = null, before = null;

            if (!LowerBound(columnPrefix, scan.Through, ref after)
                || !LowerBound(columnPrefix, scan.From, ref from)
                || !LowerBound(columnPrefix, prefixFrom, ref from))
                return null;

            if (prefixBefore != null)
            {
                if (prefixBefore.StartsWith(columnPrefix, StringComparison.Ordinal))
                {
                    before = prefixBefore[columnPrefix.Length..];
                    if (before.Length == 0)
                        return null;
                }
                else if (string.CompareOrdinal(prefixBefore, columnPrefix) < 0)
                {
                    return null;
                }
            }

            return new ObjectKeyRange(after, from, before, keyReadTypes);
        }

        /// <summary>
        /// Folds the full-key lower bound <paramref name="bound"/> into the suffix bound
        /// <paramref name="suffixBound"/> (keeping the larger). False when the bound lies
        /// above every key of the column.
        /// </summary>
        private static bool LowerBound(string columnPrefix, string? bound, ref string? suffixBound)
        {
            if (bound == null)
                return true;
            if (bound.StartsWith(columnPrefix, StringComparison.Ordinal))
            {
                var suffix = bound[columnPrefix.Length..];
                if (suffixBound == null || string.CompareOrdinal(suffix, suffixBound) > 0)
                    suffixBound = suffix;
                return true;
            }
            return string.CompareOrdinal(bound, columnPrefix) < 0;
        }

        /// <summary>
        /// The database's key order must be the C# key order, row for row: strictly
        /// ascending, inside the seek's bounds, under the column prefix. A disagreement
        /// (a dialect expression that drifted from <see cref="S3ObjectKeyMap"/>) would page
        /// out of order or skip keys, so it fails the request instead.
        /// </summary>
        private static void AssertKeyOrder(string key, string? previousKey, string columnPrefix, ObjectKeyRange range)
        {
            var suffix = key.StartsWith(columnPrefix, StringComparison.Ordinal) ? key[columnPrefix.Length..] : null;
            var inOrder = suffix != null
                && (previousKey == null || string.CompareOrdinal(key, previousKey) > 0)
                && (range.After == null || string.CompareOrdinal(suffix, range.After) > 0)
                && (range.From == null || string.CompareOrdinal(suffix, range.From) >= 0)
                && (range.Before == null || string.CompareOrdinal(suffix, range.Before) < 0);
            if (!inOrder)
                throw new InvalidOperationException(
                    "The database returned object keys out of S3 key order; the dialect's object-key expression " +
                    "does not match S3ObjectKeyMap.");
        }

        /// <summary>
        /// The read both strategies run: up to <paramref name="limit"/> rows of
        /// <paramref name="table"/> that hold a file in <paramref name="column"/>, projecting
        /// only the primary key and that column's pointer. A row without a file is no object,
        /// so it is filtered in SQL and costs no page space. The caller adds the order (a key
        /// window, or the primary key).
        /// </summary>
        private static GqlObjectQuery FileRowsQuery(
            IDbTable table, IReadOnlyList<ColumnDto> keyColumns, ColumnDto column, int limit)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Limit = limit,
                Filter = TableFilterBuilder.For(table).IsNull(column.GraphQlName, isNull: false).Build(),
            };
            foreach (var keyColumn in keyColumns)
                query.ScalarColumns.Add(new GqlObjectColumn(keyColumn.DbName, keyColumn.GraphQlName));
            if (!keyColumns.Any(k => string.Equals(k.DbName, column.DbName, StringComparison.Ordinal)))
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));
            return query;
        }

        /// <summary>
        /// Runs <paramref name="query"/> through the authorized read seam: tenant,
        /// soft-delete, row policy, and column guards all apply.
        /// </summary>
        private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(
            GqlObjectQuery query, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = _options.Endpoint,
            }, cancellationToken);
            return result.Rows;
        }

        /// <summary>The object key of <paramref name="row"/>'s file in <paramref name="column"/>.</summary>
        private static string KeyOf(
            IReadOnlyDictionary<string, object?> row, IReadOnlyList<ColumnDto> keyColumns, ColumnDto column)
        {
            var primaryKey = new object?[keyColumns.Count];
            for (var i = 0; i < keyColumns.Count; i++)
                row.TryGetValue(keyColumns[i].GraphQlName, out primaryKey[i]);
            return S3ObjectKeyMap.KeyFor(column, primaryKey);
        }

        /// <summary>
        /// Per key column, the CLR type the read pipeline returns for it, observed on one row
        /// and cached per table model. Some engines decide it from connection options the
        /// schema does not show (MySQL reads <c>TINYINT(1)</c> as <see cref="bool"/> and
        /// <c>CHAR(36)</c> as <see cref="Guid"/> by default), and the SQL key rendering must
        /// follow the value C# actually formats. An empty table caches nothing: it has no
        /// key to render.
        /// </summary>
        private async Task<IReadOnlyList<Type?>> KeyReadTypesAsync(
            IDbTable table, IReadOnlyList<ColumnDto> keyColumns, IDictionary<string, object?> userContext,
            CancellationToken cancellationToken)
        {
            if (KeyReadTypeCache.TryGetValue(table, out var cached))
                return cached;

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Limit = 1,
            };
            foreach (var keyColumn in keyColumns)
                query.ScalarColumns.Add(new GqlObjectColumn(keyColumn.DbName, keyColumn.GraphQlName));
            var result = await _reads.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = _options.Endpoint,
            }, cancellationToken);
            if (result.Rows.Count == 0)
                return new Type?[keyColumns.Count];

            var row = result.Rows[0];
            var types = keyColumns
                .Select(k => row.TryGetValue(k.GraphQlName, out var v) ? v?.GetType() : null)
                .ToArray();
            KeyReadTypeCache.AddOrUpdate(table, types);
            return types;
        }

        /// <summary>Observed key read types, keyed weakly by table model (a schema reload drops them).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDbTable, IReadOnlyList<Type?>> KeyReadTypeCache = new();

        /// <summary>
        /// The CommonPrefix <paramref name="key"/> rolls up into: the prefix plus the
        /// remainder up to and including the first delimiter, or null when there is no
        /// delimiter or the remainder holds none.
        /// </summary>
        private static string? CommonPrefixOf(string key, string prefix, string? delimiter)
        {
            if (delimiter == null)
                return null;
            var idx = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            return idx < 0 ? null : key[..(idx + delimiter.Length)];
        }

        /// <summary>
        /// The least string above every string that starts with <paramref name="value"/>:
        /// its last character incremented. Exact for object keys, which are ASCII.
        /// </summary>
        private static string Successor(string value) => value[..^1] + (char)(value[^1] + 1);

        private string Issue(string position, PageRequest request)
            => S3ContinuationToken.Issue(position, _clock.GetUtcNow(), request.Binding, _tokenSecret);

        private static S3ListObjectsPage Page(PageRequest request, PageAssembly page, string? nextToken)
            => new(
                Bucket: request.Bucket,
                Prefix: request.Prefix,
                Delimiter: request.Delimiter,
                MaxKeys: request.MaxKeys,
                IsTruncated: nextToken != null,
                Objects: page.Contents,
                CommonPrefixes: page.CommonPrefixes,
                ContinuationToken: string.IsNullOrEmpty(request.EchoContinuationToken) ? null : request.EchoContinuationToken,
                NextContinuationToken: nextToken,
                StartAfter: string.IsNullOrEmpty(request.EchoStartAfter) ? null : request.EchoStartAfter);

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
