using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// The opt-in S3-compatible HTTP front door. Enforces request size limits before any
    /// buffering, authenticates the request via <see cref="S3SigV4Verifier"/> (SigV4 header or
    /// presigned GET), and maps every failure to a deterministic S3 XML error envelope.
    ///
    /// <para>This slice is routing + auth + errors only: GetObject/PutObject data operations
    /// are deferred to later slices, so an authenticated data request answers
    /// <c>501 NotImplemented</c>. Auth is still fully enforced first, so the negative-path
    /// contract (bad signature, expired/skewed date, excessive expiry, missing/altered signed
    /// material, unknown/disabled key) is exercised without any data path existing yet.</para>
    ///
    /// <para>Only <see cref="S3ProtocolException"/> — a deliberately user-facing type with a
    /// curated message — is mapped onto the wire verbatim; every other exception maps to a
    /// generic sanitized InternalError with detail logged server-side only
    /// (.claude/rules/protocol-adapter-security.md invariants 1 and 3).</para>
    /// </summary>
    public sealed class S3Middleware
    {
        private readonly RequestDelegate _next;
        private readonly S3Options _options;
        private readonly S3SigV4Verifier _verifier;
        private readonly S3Listing _listing;
        private readonly FileObjectSeam _seam;
        private readonly ILogger<S3Middleware> _logger;

        public S3Middleware(
            RequestDelegate next,
            S3Options options,
            S3SigV4Verifier verifier,
            S3Listing listing,
            FileObjectSeam seam,
            ILogger<S3Middleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _listing = listing ?? throw new ArgumentNullException(nameof(listing));
            _seam = seam ?? throw new ArgumentNullException(nameof(seam));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var requestId = S3XmlError.NewRequestId();
            try
            {
                EnforceLimits(context.Request);

                // Cancellation is honored before any auth work so a client abort short-circuits
                // the HMAC/PBKDF2 cost.
                context.RequestAborted.ThrowIfCancellationRequested();

                var userContext = await _verifier.VerifyAsync(context.Request, context.RequestAborted);

                await DispatchAsync(context, userContext, requestId);
            }
            catch (S3ProtocolException ex)
            {
                await WriteErrorAsync(context, ex.HttpStatus, ex.Code, ex.Message, requestId);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client went away; nothing to write.
            }
            catch (Exception ex)
            {
                // Never forward an internal exception message onto the wire (invariant 3):
                // log full detail server-side, return a generic sanitized envelope.
                _logger.LogError(ex, "Unhandled error in S3 endpoint (request {RequestId}).", requestId);
                var internalError = S3ProtocolException.InternalError();
                await WriteErrorAsync(context, internalError.HttpStatus, internalError.Code, internalError.Message, requestId);
            }
        }

        /// <summary>
        /// Routes an authenticated request to the operation its path/method/query name.
        /// The service root and bucket level serve the list operations (GET only);
        /// an object-level path serves GetObject (GET), HeadObject (HEAD), and — when
        /// <see cref="S3Options.EnableWrites"/> is on — PutObject (PUT) and DeleteObject
        /// (DELETE). Every other authenticated request — bucket/service-level writes, the
        /// legacy ListObjects v1, multipart, and (when writes are off) every mutation — is a
        /// clean, authenticated 501.
        /// </summary>
        private async Task DispatchAsync(HttpContext context, IDictionary<string, object?> userContext, string requestId)
        {
            var request = context.Request;
            // Inside the mounted branch the path is the remainder after the S3 prefix:
            // "/" (or empty) is the service root, "/bucket" is a bucket, and anything
            // with a further segment addresses an object.
            var resource = (request.Path.Value ?? string.Empty).Trim('/');

            var isGet = HttpMethods.IsGet(request.Method);
            var isHead = HttpMethods.IsHead(request.Method);
            var isPut = HttpMethods.IsPut(request.Method);
            var isDelete = HttpMethods.IsDelete(request.Method);

            if (isPut || isDelete)
            {
                await DispatchWriteAsync(context, userContext, requestId, resource, isPut);
                return;
            }

            if (!isGet && !isHead)
                throw S3ProtocolException.NotImplemented();

            if (resource.Length == 0)
            {
                if (!isGet)
                    throw S3ProtocolException.NotImplemented();
                var buckets = await _listing.ListBucketsAsync(userContext, context.RequestAborted);
                var ownerId = OwnerId(userContext);
                await WriteXmlAsync(context, S3ListXml.ListAllMyBuckets(buckets, ownerId, ownerId), requestId);
                return;
            }

            var slash = resource.IndexOf('/');

            // Bucket-level: no object key (no further '/'). ListObjectsV2 only.
            if (slash < 0)
            {
                if (!isGet)
                    throw S3ProtocolException.NotImplemented();

                // ListObjectsV2 is selected by list-type=2; the legacy v1 listing is a non-goal.
                if (request.Query["list-type"].ToString() != "2")
                    throw S3ProtocolException.NotImplemented();

                var page = await _listing.ListObjectsV2Async(
                    bucket: resource,
                    prefix: NullIfEmpty(request.Query["prefix"].ToString()),
                    delimiter: NullIfEmpty(request.Query["delimiter"].ToString()),
                    maxKeys: ParseMaxKeys(request.Query["max-keys"].ToString()),
                    continuationToken: NullIfEmpty(request.Query["continuation-token"].ToString()),
                    startAfter: NullIfEmpty(request.Query["start-after"].ToString()),
                    userContext: userContext,
                    cancellationToken: context.RequestAborted);

                await WriteXmlAsync(context, S3ListXml.ListObjectsV2(page), requestId);
                return;
            }

            // Object-level: GetObject (GET) / HeadObject (HEAD).
            await GetOrHeadObjectAsync(
                context, userContext, requestId,
                bucket: resource[..slash], key: resource[(slash + 1)..], headOnly: isHead);
        }

        /// <summary>
        /// Serves GetObject/HeadObject through the authorized <see cref="FileObjectSeam"/>:
        /// the row is resolved under the caller's identity FIRST, so a missing,
        /// unauthorized, or object-less row is indistinguishable from a bad address —
        /// all answer <c>NoSuchKey</c>, never confirming existence (non-enumerating).
        /// Conditional headers (RFC 7232) are applied before any range handling; a
        /// single satisfiable range is streamed as 206, an unsatisfiable one is 416,
        /// and anything else is the whole object — always streamed, never buffered.
        /// HEAD returns the identical headers with no body.
        /// </summary>
        private async Task GetOrHeadObjectAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string bucket, string key, bool headOnly)
        {
            FileObjectSeam.ResolvedFileObject? resolved;
            try
            {
                resolved = await _seam.ResolveAsync(bucket, key, userContext, context.RequestAborted);
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError)
            {
                // Addressing fault, corrupt pointer, or a policy read-deny: an unknown
                // bucket / malformed-or-wrong-arity key / non-file column
                // (InvalidOperationException), an unparseable stored pointer, or a
                // table the caller may not read (both BifrostExecutionError — see
                // FileObjectSeam.LocateAsync and PolicyFilterTransformer). All map to
                // one non-enumerating NoSuchKey, matching the write paths: answering a
                // read-denied caller with 500 while writes answer 404 would make the
                // op class an existence/authorization oracle (the exact epic-wide
                // divergence this catch closes). The seam's ResolveAsync never touches
                // storage compensation, so FileObjectResidueException (which derives
                // from BifrostExecutionError and MUST map to 500, not NoSuchKey) cannot
                // reach here — the read path builds no blob to orphan. Detail logged only.
                _logger?.LogDebug(ex, "GetObject/HeadObject unaddressable, corrupt, or denied (request {RequestId}).", requestId);
                throw S3ProtocolException.NoSuchKey();
            }

            // A row that does not exist, is not visible to this caller, or holds no
            // object: one answer for all three (non-enumerating error policy).
            if (resolved is null)
                throw S3ProtocolException.NoSuchKey();

            var request = context.Request;

            // Conditional preconditions take effect before range handling.
            var precondition = S3ConditionalRequest.Evaluate(
                ifMatch: NullIfEmpty(request.Headers.IfMatch.ToString()),
                ifNoneMatch: NullIfEmpty(request.Headers.IfNoneMatch.ToString()),
                ifModifiedSince: NullIfEmpty(request.Headers.IfModifiedSince.ToString()),
                ifUnmodifiedSince: NullIfEmpty(request.Headers.IfUnmodifiedSince.ToString()),
                etag: resolved.ETag,
                lastModified: resolved.LastModified);

            if (precondition == S3PreconditionOutcome.PreconditionFailed)
                throw S3ProtocolException.PreconditionFailed();
            if (precondition == S3PreconditionOutcome.NotModified)
            {
                WriteNotModified(context, resolved, requestId);
                return;
            }

            var length = resolved.ContentLength;
            var range = S3RangeParser.Parse(NullIfEmpty(request.Headers.Range.ToString()), length);
            if (range.Kind == S3RangeKind.Unsatisfiable)
            {
                await WriteRangeNotSatisfiableAsync(context, length, requestId);
                return;
            }

            var partial = range.Kind == S3RangeKind.Satisfiable;
            var start = partial ? range.Start : 0;
            var count = partial ? range.Length : length;

            WriteObjectHeaders(context, resolved, requestId, start, count, length, partial);
            context.Response.StatusCode = partial ? 206 : 200;

            // HEAD carries the same headers with no body.
            if (headOnly)
                return;

            await _seam.CopyContentToAsync(resolved, context.Response.Body, start, count, context.RequestAborted);
        }

        /// <summary>
        /// Routes a write verb (PUT/DELETE). The write gate is the FIRST thing checked —
        /// before the body is read, the address is parsed, or any mutation intent is built —
        /// so a disabled write surface does nothing at all and cannot be probed for behaviour
        /// (fail-closed by construction; .claude/rules/protocol-adapter-security.md invariant 7).
        /// Only object-level writes are served; bucket/service-level writes (CreateBucket,
        /// DeleteBucket) are a non-goal and answer 501.
        /// </summary>
        private async Task DispatchWriteAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string resource, bool isPut)
        {
            // GATE — off by default. No body read, no address parse, no intent.
            if (!_options.EnableWrites)
                throw S3ProtocolException.NotImplemented();

            var slash = resource.IndexOf('/');
            if (slash < 0)
                // A write with no object key addresses a bucket/the service; not a goal.
                throw S3ProtocolException.NotImplemented();

            var bucket = resource[..slash];
            var key = resource[(slash + 1)..];

            if (isPut)
            {
                // A PUT carrying x-amz-copy-source is CopyObject (server-side copy), not a
                // body upload; it is dispatched separately so the source is read through the
                // authorized read seam before the destination is written.
                if (context.Request.Headers.ContainsKey("x-amz-copy-source"))
                    await CopyObjectAsync(context, userContext, requestId, bucket, key);
                else
                    await PutObjectAsync(context, userContext, requestId, bucket, key);
            }
            else
                await DeleteObjectAsync(context, userContext, requestId, bucket, key);
        }

        /// <summary>
        /// Serves PutObject: validates the payload (length, single-part SHA-256, user-metadata
        /// limits), then writes through the authorized <see cref="FileObjectSeam"/> — hence the
        /// full mutation pipeline (tenant scoping, audit, soft-delete, encryption-on-write,
        /// CDC/history), with the seam supplying only the positional PK and the caller's
        /// identity and building no predicate of its own. The body is bounded to
        /// <see cref="S3Options.MaxBodyBytes"/> as it is read, so an undeclared or lying
        /// Content-Length cannot make the endpoint buffer without limit.
        ///
        /// <para>Chunked/streaming SigV4, multipart, and server-side copy are non-goals and
        /// answer 501. A row the caller cannot see or write is indistinguishable from a missing
        /// key (NoSuchKey), so a cross-tenant or forged address is a non-enumerating 404 and
        /// never reaches the storage provider — the seam vetoes before any blob is touched.</para>
        /// </summary>
        private async Task PutObjectAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string bucket, string key)
        {
            var request = context.Request;

            // Non-goals rejected before the body is read.
            var declaredHash = request.Headers["x-amz-content-sha256"].ToString();
            if (declaredHash.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase))
                throw S3ProtocolException.NotImplemented("Chunked/streaming uploads are not supported.");
            if (request.Query.ContainsKey("uploads") || request.Query.ContainsKey("uploadId")
                || request.Query.ContainsKey("partNumber"))
                throw S3ProtocolException.NotImplemented("Multipart upload is not supported.");

            // A non-chunked PutObject must declare its length; without it there is no
            // completeness check and no signed-length invariant to hold to.
            if (request.ContentLength is not { } declaredLength)
                throw S3ProtocolException.MissingContentLength();

            var contentType = NullIfEmpty(request.Headers.ContentType.ToString());
            var customMetadata = ReadUserMetadata(request);

            var content = await ReadBoundedBodyAsync(request.Body, context.RequestAborted);

            // The bytes actually received must match the declared (and signed) length: a
            // truncated or over-long body is an IncompleteBody, never a silently stored
            // partial object.
            if (content.LongLength != declaredLength)
                throw S3ProtocolException.IncompleteBody();

            // Single-part integrity: when the client declares a concrete payload hash it must
            // match the bytes. UNSIGNED-PAYLOAD opts out of the check (the signature still
            // authenticated the request); a streaming marker was already rejected above.
            if (!string.IsNullOrEmpty(declaredHash)
                && !declaredHash.Equals(S3SigV4.UnsignedPayload, StringComparison.OrdinalIgnoreCase))
            {
                var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!actualHash.Equals(declaredHash, StringComparison.OrdinalIgnoreCase))
                    throw S3ProtocolException.ContentSha256Mismatch();
            }

            FileObjectSeam.ResolvedFileObject resolved;
            try
            {
                resolved = await _seam.PutAsync(
                    bucket, key, content, contentType, customMetadata, userContext, context.RequestAborted);
            }
            catch (FileObjectResidueException ex)
            {
                // Post-authorization internal failure that ORPHANED a blob (the pointer write
                // failed AND the compensating rollback failed too). The operator must reclaim the
                // orphan, so log at Error WITH the storage key — never Debug, which is invisible at
                // production levels. The wire gets a sanitized 500: the seam message embeds the
                // storage key and is not wire-safe (invariant 3). NoSuchKey is wrong here — it
                // would misreport a broken server as a missing key.
                _logger.LogError(
                    ex, "PutObject left orphaned storage residue at key '{StorageKey}' (request {RequestId}).",
                    ex.StorageKey, requestId);
                throw S3ProtocolException.InternalError();
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError)
            {
                // Denial/unaddressable/corrupt-pointer: an addressing fault (unknown bucket, wrong
                // key arity, non-file column), a row the caller cannot see or write (cross-tenant /
                // scoped-away), or an unparseable pointer. One non-enumerating 404 — responding
                // differently to any of these would weaken non-enumeration — and, critically, the
                // pipeline vetoed before the provider was ever asked to store anything (invariant
                // 8a). Detail logged only. (The residue type is caught above; it derives from
                // BifrostExecutionError, so that catch MUST precede this one.)
                _logger?.LogDebug(ex, "PutObject denied or unaddressable (request {RequestId}).", requestId);
                throw S3ProtocolException.NoSuchKey();
            }

            var response = context.Response;
            response.StatusCode = 200;
            response.Headers["x-amz-request-id"] = requestId;
            // The ETag is the single-part rule: the MD5 the pipeline persisted at write time,
            // read straight back from the stored object rather than recomputed.
            if (!string.IsNullOrEmpty(resolved.ETag))
                response.Headers.ETag = $"\"{resolved.ETag}\"";
            response.ContentLength = 0;
        }

        /// <summary>
        /// Serves CopyObject (same-service): reads the source through the authorized read
        /// seam and writes the destination through the full put/mutation path, with the two
        /// authorizations independent and BOTH required before any destination write.
        ///
        /// <para>Order is deliberate. The source is resolved FIRST under the caller's identity
        /// (<see cref="FileObjectSeam.ResolveAsync"/>) — a source the caller cannot see,
        /// that does not exist, or that holds no object is a single non-enumerating
        /// <c>NoSuchKey</c>, exactly as GetObject answers, and the destination is never
        /// touched. Only once the source read has passed is the destination written via
        /// <see cref="FileObjectSeam.PutAsync"/>, which runs the destination's OWN
        /// tenant/permission gate and the full mutation pipeline, stores the bytes on a fresh
        /// random storage key, and compensates on veto (invariant 8a) — so a caller who can
        /// read the source but not write the destination (or vice versa) copies nothing.</para>
        ///
        /// <para>Metadata directive: <c>COPY</c> (default) inherits the source's content type
        /// and user metadata; <c>REPLACE</c> takes them from this request. A self-copy
        /// (source == destination) is only legal under <c>REPLACE</c> — a <c>COPY</c>
        /// self-copy is the S3 <c>InvalidRequest</c> no-op. Multipart copy, versioned
        /// sources, and cross-deployment/external URLs are non-goals.</para>
        /// </summary>
        private async Task CopyObjectAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string destBucket, string destKey)
        {
            var request = context.Request;

            // UploadPartCopy (multipart) is a non-goal.
            if (request.Query.ContainsKey("uploadId") || request.Query.ContainsKey("partNumber"))
                throw S3ProtocolException.NotImplemented("Multipart copy is not supported.");

            var (srcBucket, srcKey) = S3CopySource.Parse(request.Headers["x-amz-copy-source"].ToString());

            // --- source-read authorization (independent of the destination) ---
            // Resolve the source under the caller's identity through the authorized read seam
            // FIRST, and fully, before the destination is touched at all. A missing,
            // unauthorized (cross-tenant), or object-less source is one non-enumerating
            // NoSuchKey — the same answer GetObject gives, so the copy is not an existence
            // oracle for objects the caller cannot read.
            FileObjectSeam.ResolvedFileObject source;
            try
            {
                var resolved = await _seam.ResolveAsync(srcBucket, srcKey, userContext, context.RequestAborted);
                if (resolved is null)
                    throw S3ProtocolException.NoSuchKey();
                source = resolved;
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError)
            {
                // Source addressing fault, corrupt pointer, or policy read-deny — the same
                // read-seam failure family GetObject handles, mapped to the same
                // non-enumerating NoSuchKey so a copy is not an existence/authorization
                // oracle for a source the caller cannot read. ResolveAsync builds no blob,
                // so FileObjectResidueException (→ 500) cannot arise on this read; only the
                // destination write below (PutAsync) can, and it is caught there. Detail
                // logged only.
                _logger?.LogDebug(ex, "CopyObject source unaddressable, corrupt, or denied (request {RequestId}).", requestId);
                throw S3ProtocolException.NoSuchKey();
            }

            // The source is materialized in memory before being handed to the destination
            // write, so an object larger than the body cap is rejected rather than buffered.
            if (source.ContentLength > _options.MaxBodyBytes)
                throw S3ProtocolException.EntityTooLarge();

            // Metadata directive: COPY (default) inherits from the source; REPLACE takes the
            // content type and user metadata from this request. An unknown value is a client error.
            var directive = NullIfEmpty(request.Headers["x-amz-metadata-directive"].ToString());
            bool replace;
            if (directive is null || directive.Equals("COPY", StringComparison.OrdinalIgnoreCase))
                replace = false;
            else if (directive.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
                replace = true;
            else
                throw S3ProtocolException.InvalidArgument("x-amz-metadata-directive must be COPY or REPLACE.");

            // Self-copy is only legal when it changes metadata (REPLACE). A COPY self-copy is
            // a no-op S3 rejects as InvalidRequest.
            if (!replace && string.Equals(srcBucket, destBucket, StringComparison.Ordinal)
                && string.Equals(srcKey, destKey, StringComparison.Ordinal))
                throw S3ProtocolException.InvalidRequest(
                    "This copy request is illegal because it is trying to copy an object to itself " +
                    "without changing the object's metadata.");

            var contentType = replace ? NullIfEmpty(request.Headers.ContentType.ToString()) : source.ContentType;
            IReadOnlyDictionary<string, string>? customMetadata = replace
                ? ReadUserMetadata(request)
                : source.CustomMetadata.Count == 0 ? null : source.CustomMetadata;

            var content = await _seam.GetContentAsync(source, context.RequestAborted);

            // --- destination-write authorization + write ---
            // PutAsync applies the destination's own tenant/permission gate and the full
            // mutation pipeline, writes the bytes to a FRESH random storage key, compensates
            // on veto (invariant 8a), and detects a scoped-away write via AffectedRows (8b).
            // Source read has already passed, so BOTH checks hold before the destination
            // pointer commits.
            FileObjectSeam.ResolvedFileObject stored;
            try
            {
                stored = await _seam.PutAsync(
                    destBucket, destKey, content, contentType, customMetadata, userContext, context.RequestAborted);
            }
            catch (FileObjectResidueException ex)
            {
                // Post-authorization internal failure that ORPHANED a blob: log at Error WITH
                // the storage key and return a sanitized 500 (the seam message embeds the key
                // and is not wire-safe, invariant 3). Same contract as PutObject. The residue
                // type derives from BifrostExecutionError, so this catch MUST precede the one below.
                _logger.LogError(
                    ex, "CopyObject left orphaned storage residue at key '{StorageKey}' (request {RequestId}).",
                    ex.StorageKey, requestId);
                throw S3ProtocolException.InternalError();
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError)
            {
                // Destination denial/unaddressable/scoped-away: one non-enumerating NoSuchKey,
                // and the pipeline vetoed before the provider stored the destination pointer
                // (invariant 8a). Detail logged only.
                _logger?.LogDebug(ex, "CopyObject destination denied or unaddressable (request {RequestId}).", requestId);
                throw S3ProtocolException.NoSuchKey();
            }

            var response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "application/xml";
            response.Headers["x-amz-request-id"] = requestId;
            // The ETag lives in the CopyObjectResult body (not a response header), matching S3:
            // it is the destination's newly persisted single-part MD5, read back from the store.
            await response.WriteAsync(
                S3ListXml.CopyObjectResult(stored.ETag, stored.LastModified), context.RequestAborted);
        }

        /// <summary>
        /// Serves DeleteObject: routes the removal through the authorized seam (which clears
        /// the row's file pointer via the mutation pipeline first, then reclaims the blob —
        /// never a direct provider delete, never a predicate built here). S3 delete is
        /// idempotent, so a missing object, a cross-tenant/forged address (vetoed by the
        /// pipeline before the provider is touched), or a malformed key all answer the same
        /// 204 No Content — non-enumerating, and destroying nothing the caller was not
        /// authorized to remove.
        /// </summary>
        private async Task DeleteObjectAsync(
            HttpContext context, IDictionary<string, object?> userContext, string requestId,
            string bucket, string key)
        {
            if (context.Request.Query.ContainsKey("uploadId"))
                throw S3ProtocolException.NotImplemented("Multipart upload is not supported.");

            try
            {
                await _seam.DeleteAsync(bucket, key, userContext, context.RequestAborted);
            }
            catch (FileObjectResidueException ex)
            {
                // The pointer was cleared (committed) but the blob delete then failed: the object is
                // now unreferenced residue an operator must reclaim. DELETE is idempotent, so the
                // wire still answers 204 — but the orphan is NOT swallowed at Debug: log it at Error
                // WITH the storage key so it can be found and collected.
                _logger.LogError(
                    ex, "DeleteObject left orphaned storage residue at key '{StorageKey}' (request {RequestId}).",
                    ex.StorageKey, requestId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or BifrostExecutionError)
            {
                // Unaddressable or cross-tenant/scoped-away denial: not a client-facing failure.
                // Delete is idempotent, so the answer is the same 204; detail logged only. (The
                // residue type was caught above; it derives from BifrostExecutionError, so its
                // catch MUST precede this one.)
                _logger?.LogDebug(ex, "DeleteObject unaddressable or denied (request {RequestId}).", requestId);
            }

            context.Response.StatusCode = 204;
            context.Response.Headers["x-amz-request-id"] = requestId;
        }

        /// <summary>
        /// Reads the request body into a buffer, bounded to <see cref="S3Options.MaxBodyBytes"/>
        /// as it streams so an undeclared/oversized/lying Content-Length can never make the
        /// endpoint buffer past the cap. (The seam takes a materialized byte[]; this bounds the
        /// materialization rather than eliminating it — see FileStorageService's remarks.)
        /// </summary>
        private async Task<byte[]> ReadBoundedBodyAsync(Stream body, CancellationToken cancellationToken)
        {
            var cap = _options.MaxBodyBytes;
            using var buffer = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(rented.AsMemory(), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > cap)
                        throw S3ProtocolException.EntityTooLarge();
                    buffer.Write(rented, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Collects the request's <c>x-amz-meta-*</c> headers into the user-metadata dictionary
        /// persisted with the object. Every key and value must be printable US-ASCII (so it can
        /// round-trip as a response header without a 500 or a header-injection risk) and the
        /// summed size is capped at <see cref="S3Options.MaxMetadataBytes"/>. Returns null when
        /// the request carries no user metadata.
        /// </summary>
        private IReadOnlyDictionary<string, string>? ReadUserMetadata(HttpRequest request)
        {
            Dictionary<string, string>? metadata = null;
            long totalBytes = 0;
            const string prefix = "x-amz-meta-";

            foreach (var header in request.Headers)
            {
                if (!header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = header.Key[prefix.Length..];
                var value = header.Value.ToString();
                if (name.Length == 0)
                    throw S3ProtocolException.InvalidArgument("A user-metadata header name is empty.");
                if (!IsHeaderSafeAscii(name) || !IsHeaderSafeAscii(value))
                    throw S3ProtocolException.InvalidArgument("User metadata must be printable US-ASCII.");

                totalBytes += name.Length + value.Length;
                if (totalBytes > _options.MaxMetadataBytes)
                    throw S3ProtocolException.InvalidArgument("Your metadata headers exceed the maximum allowed metadata size.");

                metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                metadata[name] = value;
            }

            return metadata;
        }

        // Printable ASCII only (0x20..0x7E): no control chars (CR/LF injection) and no
        // non-ASCII that cannot survive the response-header round trip.
        private static bool IsHeaderSafeAscii(string value)
        {
            foreach (var c in value)
                if (c is < ' ' or > '~')
                    return false;
            return true;
        }

        /// <summary>
        /// Writes the common object headers for a 200/206 (and the HEAD equivalents):
        /// content type, the served byte count, ETag, last-modified, Accept-Ranges,
        /// persisted user metadata, and — for a partial response — Content-Range.
        /// </summary>
        private static void WriteObjectHeaders(
            HttpContext context, FileObjectSeam.ResolvedFileObject obj, string requestId,
            long start, long count, long totalLength, bool partial)
        {
            var response = context.Response;
            response.ContentType = string.IsNullOrEmpty(obj.ContentType) ? "application/octet-stream" : obj.ContentType;
            response.ContentLength = count;
            response.Headers.AcceptRanges = "bytes";
            response.Headers["x-amz-request-id"] = requestId;
            SetValidatorHeaders(response, obj);
            WriteUserMetadata(response, obj);

            if (partial)
                response.Headers.ContentRange = $"bytes {start}-{start + count - 1}/{totalLength}";
        }

        private static void WriteNotModified(HttpContext context, FileObjectSeam.ResolvedFileObject obj, string requestId)
        {
            var response = context.Response;
            response.StatusCode = 304;
            response.Headers["x-amz-request-id"] = requestId;
            response.Headers.AcceptRanges = "bytes";
            SetValidatorHeaders(response, obj);
            // 304 carries no body and no Content-Length.
        }

        private async Task WriteRangeNotSatisfiableAsync(HttpContext context, long totalLength, string requestId)
        {
            if (context.Response.HasStarted)
                return;

            context.Response.Clear();
            context.Response.StatusCode = 416;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            // Advertise the valid extent so a client can re-request within bounds.
            context.Response.Headers.ContentRange = $"bytes */{totalLength}";
            await context.Response.WriteAsync(
                S3XmlError.Write("InvalidRange", "The requested range is not satisfiable.", requestId),
                context.RequestAborted);
        }

        private static void SetValidatorHeaders(HttpResponse response, FileObjectSeam.ResolvedFileObject obj)
        {
            if (!string.IsNullOrEmpty(obj.ETag))
                response.Headers.ETag = $"\"{obj.ETag}\"";
            response.Headers.LastModified =
                DateTime.SpecifyKind(obj.LastModified, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Emits the object's persisted custom metadata as <c>x-amz-meta-*</c> response
        /// headers. An entry whose key or value carries a CR/LF (or other control
        /// character) is skipped rather than written, so a poisoned pointer cannot
        /// inject a header or split the response.
        /// </summary>
        private static void WriteUserMetadata(HttpResponse response, FileObjectSeam.ResolvedFileObject obj)
        {
            foreach (var (name, value) in obj.CustomMetadata)
            {
                if (string.IsNullOrEmpty(name) || ContainsControlChar(name) || ContainsControlChar(value))
                    continue;
                response.Headers["x-amz-meta-" + name] = value;
            }
        }

        private static bool ContainsControlChar(string? value)
        {
            if (value is null)
                return false;
            foreach (var c in value)
                if (char.IsControl(c))
                    return true;
            return false;
        }

        private static int? ParseMaxKeys(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            // TryParse, never int.Parse: a malformed or out-of-range value maps to a
            // clean protocol error, never an escaping exception (invariant 5).
            if (!int.TryParse(raw, out var value))
                throw S3ProtocolException.InvalidArgument("max-keys is not a valid integer.");
            return value;
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string OwnerId(IDictionary<string, object?> userContext)
            => userContext.TryGetValue("user_id", out var id) && id is not null
                ? id.ToString() ?? "bifrost"
                : "bifrost";

        private static async Task WriteXmlAsync(HttpContext context, string xml, string requestId)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            await context.Response.WriteAsync(xml, context.RequestAborted);
        }

        /// <summary>
        /// Enforces URL, header, and body size caps BEFORE any payload is buffered. The body
        /// cap is checked from Content-Length so an oversized upload is rejected without
        /// reading it (invariant: bound the cost of adversarial input up front).
        /// </summary>
        private void EnforceLimits(HttpRequest request)
        {
            var urlLength = (request.PathBase.Value?.Length ?? 0)
                + (request.Path.Value?.Length ?? 0)
                + (request.QueryString.Value?.Length ?? 0);
            if (urlLength > _options.MaxUrlLength)
                throw S3ProtocolException.InvalidArgument("Request URL is too long.");

            long headerBytes = 0;
            foreach (var header in request.Headers)
            {
                headerBytes += header.Key.Length;
                foreach (var value in header.Value)
                    headerBytes += value?.Length ?? 0;
                if (headerBytes > _options.MaxHeaderBytes)
                    throw S3ProtocolException.InvalidArgument("Request headers are too large.");
            }

            if (request.ContentLength is { } length && length > _options.MaxBodyBytes)
                throw S3ProtocolException.EntityTooLarge();
        }

        private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message, string requestId)
        {
            if (context.Response.HasStarted)
                return;

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/xml";
            context.Response.Headers["x-amz-request-id"] = requestId;
            await context.Response.WriteAsync(S3XmlError.Write(code, message, requestId), context.RequestAborted);
        }
    }
}
