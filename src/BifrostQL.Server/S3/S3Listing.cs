using System.Collections;
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
    /// bucket name, and (c) is READABLE by the caller under the SAME
    /// <see cref="PolicyEvaluator"/> gate the query path enforces — reconstructed
    /// through the shared <see cref="PolicyIdentity"/> projection, fail-closed on any
    /// evaluation fault (see .claude/rules/protocol-adapter-security.md invariant 4 and
    /// the pgwire catalog precedent <c>PgCatalogVisibility</c>). A table with no file
    /// column, a filesystem root, or a table the caller cannot read is never listed —
    /// so bucket enumeration is not an information-disclosure side channel.</para>
    ///
    /// <para><b>Objects resolve through the authorized read seam.</b> Rows come from
    /// <see cref="IQueryIntentExecutor"/>, so tenant isolation, soft-delete, row-scope
    /// policy, and column read guards all apply unconditionally — the lister builds no
    /// tenant/policy predicate of its own; the only predicate it constructs is ordering
    /// and it reads only the file-column pointer and primary key. A read-denied file
    /// column comes back null and simply yields no object.</para>
    ///
    /// <para><b>Pagination is stable and bounded.</b> Objects are ordered by object key
    /// (S3's ascending UTF-8 order; our keys are percent-encoded ASCII, so ordinal
    /// string order is that byte order), prefix/delimiter grouping matches S3, and the
    /// page is capped by <c>max-keys</c>. The resume position travels in an opaque,
    /// integrity-protected <see cref="S3ContinuationToken"/> bound to the bucket,
    /// prefix, delimiter, page size, and caller identity, so a tampered or cross-bucket
    /// token fails closed.</para>
    ///
    /// <para><b>Bound:</b> a page is computed by materializing the bucket's scoped,
    /// prefix-filtered candidate set and slicing it — bounded by
    /// <see cref="S3Options.MaxListMaterialize"/>. A bucket with more addressable
    /// objects than that cap fails fast with a curated error rather than silently
    /// truncating a listing (which would lie about completeness). Keyset pagination over
    /// a database-computed key is the scale path beyond this slice.</para>
    /// </summary>
    public sealed class S3Listing
    {
        private static readonly PolicyEvaluator Evaluator = new();
        // A stable creation date for synthesized buckets: a table has none, and S3
        // clients do not rely on this value for correctness. Fixed rather than
        // "now" so the listing is deterministic.
        private static readonly DateTime BucketCreationDate = DateTime.UnixEpoch;

        private readonly IQueryIntentExecutor _reads;
        private readonly FileStorageService _storage;
        private readonly S3Options _options;
        private readonly ILogger? _logger;
        private readonly byte[] _tokenSecret;

        public S3Listing(
            IQueryIntentExecutor reads,
            S3Options options,
            FileStorageService? storage = null,
            ILogger<S3Listing>? logger = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _storage = storage ?? new FileStorageService();
            _logger = logger;

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
            var identity = PolicyIdentity.FromUserContext(userContext);

            var buckets = new List<S3BucketInfo>();
            foreach (var table in model.Tables)
            {
                if (!HasFileColumn(table, model))
                    continue;

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

                if (!CanRead(table, identity))
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
            var identity = PolicyIdentity.FromUserContext(userContext);

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
            // file-configured bucket, is reported as absent — never AccessDenied, which
            // would confirm it exists.
            var fileColumns = FileColumns(table, model);
            if (fileColumns.Count == 0 || !CanRead(table, identity))
                throw S3ProtocolException.NoSuchBucket();

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
                resumeAfter = S3ContinuationToken.Decode(continuationToken, binding, _tokenSecret);
            else
                resumeAfter = string.IsNullOrEmpty(startAfter) ? null : startAfter;

            var allObjects = await MaterializeAsync(table, fileColumns, effectivePrefix, userContext, cancellationToken);

            return BuildPage(
                bucket, allObjects, effectivePrefix, delimiter, effectiveMaxKeys,
                resumeAfter, continuationToken, startAfter, binding);
        }

        // ---- materialization ------------------------------------------------------

        /// <summary>
        /// Reads every addressable object in the bucket through the authorized read
        /// seam: the primary key plus each file-storage column, ordered by primary key
        /// for a deterministic scan. Rows whose file column is null hold no object and
        /// are skipped. Bounded by <see cref="S3Options.MaxListMaterialize"/>.
        /// </summary>
        private async Task<List<S3ObjectInfo>> MaterializeAsync(
            IDbTable table, IReadOnlyList<ColumnDto> fileColumns, string prefix,
            IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();
            if (keyColumns.Count == 0)
                // A keyless table has no addressable rows; treat it as an empty bucket
                // rather than throwing (it survived the file-column check by config).
                return new List<S3ObjectInfo>();

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Sort = keyColumns.Select(c => $"{c.GraphQlName}_asc").ToList(),
                // One past the cap, so we can detect (and fail fast on) a bucket that
                // exceeds what this slice can page in memory.
                Limit = _options.MaxListMaterialize + 1,
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

            if (result.Rows.Count > _options.MaxListMaterialize)
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
                nextToken = S3ContinuationToken.Issue(position, binding, _tokenSecret);
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

        private bool HasFileColumn(IDbTable table, IDbModel model)
        {
            foreach (var column in table.Columns)
                if (_storage.IsFileStorageColumn(table, column, model))
                    return true;
            return false;
        }

        private IReadOnlyList<ColumnDto> FileColumns(IDbTable table, IDbModel model)
        {
            var result = new List<ColumnDto>();
            foreach (var column in table.Columns)
                if (_storage.IsFileStorageColumn(table, column, model))
                    result.Add(column);
            return result;
        }

        private static bool CanRead(IDbTable table, AppIdentity identity)
        {
            try
            {
                var policy = PolicyConfigCollector.FromTable(table);
                return Evaluator.CanAct(policy, PolicyAction.Read, identity).Allowed;
            }
            catch
            {
                // Fail closed: a table whose policy cannot be parsed/evaluated is hidden.
                return false;
            }
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
            var parts = new List<string>();
            foreach (var kv in userContext)
            {
                var rendered = RenderClaim(kv.Value);
                if (rendered is not null)
                    parts.Add(kv.Key + "=" + rendered);
            }
            parts.Sort(StringComparer.Ordinal);

            var canonical = string.Join("\n", parts);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static string? RenderClaim(object? value) => value switch
        {
            null => null,
            string s => s,
            bool or byte or short or int or long or Guid =>
                Convert.ToString(value, CultureInfo.InvariantCulture),
            IEnumerable<string> seq => "[" + string.Join(",", seq) + "]",
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(x => x?.ToString() ?? "")) + "]",
            _ => null, // opaque/complex entries are not part of the stable identity shape
        };
    }
}
