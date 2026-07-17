using System.Security.Claims;
using BifrostQL.Server.Auth;
using BifrostQL.Server.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Constructs validly-signed SigV4 requests (header authorization and presigned GET) using
    /// the SAME <see cref="S3SigV4"/> canonicalization the verifier uses, so a correct signature
    /// agrees by construction and a deliberately tampered request is a genuine mismatch — never a
    /// signer/verifier drift masquerading as one. Tests mutate the returned context to build the
    /// reject cases.
    /// </summary>
    internal static class S3TestSigner
    {
        public const string AccessKeyId = "AKIDEXAMPLE";
        public const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        public const string Region = "us-east-1";
        // SHA-256 of the empty payload.
        public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        public static DefaultHttpContext BuildHeaderSigned(
            string method = "GET",
            string path = "/bucket/object.txt",
            string host = "s3.example.com",
            string? query = null,
            DateTimeOffset? signTime = null,
            string secret = Secret,
            string accessKeyId = AccessKeyId,
            string region = Region,
            string payloadHash = EmptyPayloadHash,
            IServiceProvider? services = null)
        {
            var when = (signTime ?? DateTimeOffset.UtcNow).UtcDateTime;
            var amzDate = when.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = when.ToString("yyyyMMdd");

            var ctx = NewContext(method, path, host, services);
            if (query is not null) ctx.Request.QueryString = new QueryString(query);
            ctx.Request.Headers["x-amz-date"] = amzDate;
            ctx.Request.Headers["x-amz-content-sha256"] = payloadHash;

            const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            var canonicalHeaders = new List<(string, string)>
            {
                ("host", host),
                ("x-amz-content-sha256", payloadHash),
                ("x-amz-date", amzDate),
            };
            var canonicalQuery = S3SigV4.CanonicalQueryString(QueryPairs(ctx.Request));
            var signature = Sign(method, path, canonicalQuery, canonicalHeaders, signedHeaders,
                payloadHash, amzDate, dateStamp, region, secret);

            ctx.Request.Headers["Authorization"] =
                $"{S3SigV4.Algorithm} Credential={accessKeyId}/{dateStamp}/{region}/s3/aws4_request, " +
                $"SignedHeaders={signedHeaders}, Signature={signature}";
            return ctx;
        }

        public static DefaultHttpContext BuildPresigned(
            string method = "GET",
            string path = "/bucket/object.txt",
            string host = "s3.example.com",
            DateTimeOffset? signTime = null,
            int expires = 3600,
            string secret = Secret,
            string accessKeyId = AccessKeyId,
            string region = Region,
            IServiceProvider? services = null)
        {
            var when = (signTime ?? DateTimeOffset.UtcNow).UtcDateTime;
            var amzDate = when.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = when.ToString("yyyyMMdd");
            var credential = $"{accessKeyId}/{dateStamp}/{region}/s3/aws4_request";
            const string signedHeaders = "host";

            var pairs = new List<KeyValuePair<string, string>>
            {
                new("X-Amz-Algorithm", S3SigV4.Algorithm),
                new("X-Amz-Credential", credential),
                new("X-Amz-Date", amzDate),
                new("X-Amz-Expires", expires.ToString()),
                new("X-Amz-SignedHeaders", signedHeaders),
            };

            var ctx = NewContext(method, path, host, services);
            var canonicalHeaders = new List<(string, string)> { ("host", host) };
            var canonicalQuery = S3SigV4.CanonicalQueryString(pairs);
            var signature = Sign(method, path, canonicalQuery, canonicalHeaders, signedHeaders,
                S3SigV4.UnsignedPayload, amzDate, dateStamp, region, secret);

            pairs.Add(new KeyValuePair<string, string>("X-Amz-Signature", signature));
            ctx.Request.QueryString = new QueryBuilder(pairs).ToQueryString();
            return ctx;
        }

        private static DefaultHttpContext NewContext(string method, string path, string host, IServiceProvider? services)
        {
            var ctx = new DefaultHttpContext();
            if (services is not null) ctx.RequestServices = services;
            ctx.Request.Method = method;
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString(host);
            ctx.Request.Path = path;
            ctx.Request.Headers["Host"] = host;
            return ctx;
        }

        private static string Sign(
            string method, string path, string canonicalQuery,
            IReadOnlyList<(string, string)> canonicalHeaders, string signedHeaders,
            string payloadHash, string amzDate, string dateStamp, string region, string secret)
        {
            var canonicalRequest = S3SigV4.BuildCanonicalRequest(
                method, S3SigV4.CanonicalUri(path), canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
            var scope = S3SigV4.CredentialScope(dateStamp, region);
            var stringToSign = S3SigV4.StringToSign(amzDate, scope, canonicalRequest);
            var signingKey = S3SigV4.DeriveSigningKey(secret, dateStamp, region);
            return S3SigV4.ComputeSignatureHex(signingKey, stringToSign);
        }

        private static IEnumerable<KeyValuePair<string, string>> QueryPairs(HttpRequest request)
        {
            foreach (var kv in request.Query)
                foreach (var v in kv.Value)
                    yield return new KeyValuePair<string, string>(kv.Key, v ?? string.Empty);
        }

        public static ClaimsPrincipal Principal(string subject = "s3-user", string? tenant = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            if (tenant is not null) claims.Add(new Claim(LocalAuthClaims.Tenant, tenant));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "s3"));
        }

        /// <summary>A principal that authenticates but carries no subject claim — must fail closed on projection.</summary>
        public static ClaimsPrincipal SubjectlessPrincipal()
            => new(new ClaimsIdentity(Array.Empty<Claim>(), authenticationType: "s3"));
    }

    /// <summary>In-memory access-key store for tests; unknown ids resolve to null (never a fallback).</summary>
    internal sealed class FakeS3AccessKeyStore : IS3AccessKeyStore
    {
        private readonly Dictionary<string, S3AccessKey> _keys = new(StringComparer.Ordinal);

        public FakeS3AccessKeyStore Add(string accessKeyId, string secret, ClaimsPrincipal principal, bool enabled = true)
        {
            _keys[accessKeyId] = new S3AccessKey(accessKeyId, secret, principal, enabled);
            return this;
        }

        public Task<S3AccessKey?> FindAsync(string accessKeyId, CancellationToken cancellationToken)
            => Task.FromResult(_keys.TryGetValue(accessKeyId, out var key) ? key : null);
    }
}
