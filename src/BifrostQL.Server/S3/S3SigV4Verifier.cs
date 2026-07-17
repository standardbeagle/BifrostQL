using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace BifrostQL.Server.S3
{
    /// <summary>
    /// Verifies an AWS Signature Version 4 request (header authorization or presigned GET
    /// query) against a resolved access-key secret, then projects the resolved principal
    /// through <see cref="IBifrostAuthContextFactory"/> — the same identity seam every other
    /// transport gate uses, fail-closed.
    ///
    /// <para>Security posture (see .claude/rules/protocol-adapter-security.md):</para>
    /// <list type="bullet">
    /// <item>The signature comparison runs UNCONDITIONALLY against a decoy secret when the
    /// access key is unknown or disabled, so an unknown key is indistinguishable by timing or
    /// response from a known key with a wrong signature (invariant 2). The existence/enabled
    /// check is ANDed AFTER the constant-time compare, never gated before it.</item>
    /// <item>Every client-fault path throws <see cref="S3ProtocolException"/> — the single type
    /// the middleware's catch filters on — so nothing escapes to the host on adversarial input
    /// (invariant 1). Untrusted numeric/date parsing uses TryParse, never a throwing parser
    /// (invariant 5).</item>
    /// <item>No credential, canonical string, path, or tenant data ever reaches the wire; only
    /// fixed, curated messages are thrown (invariant 3).</item>
    /// </list>
    /// </summary>
    public sealed class S3SigV4Verifier
    {
        // A fixed, non-secret decoy used to keep the HMAC + compare work identical for an
        // unknown/disabled key. Its only requirement is that a real client cannot know it,
        // which holds because it never leaves the process and no key is provisioned with it.
        private const string DecoySecret = "bifrost-s3-decoy-secret-not-a-real-credential";
        private const string AmzDateFormat = "yyyyMMddTHHmmssZ";

        private static readonly string[] RequiredHeaderAuthSignedHeaders = { "host", "x-amz-date" };

        private readonly IS3AccessKeyStore _store;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly S3Options _options;
        private readonly TimeProvider _clock;
        private readonly ILogger? _logger;

        public S3SigV4Verifier(
            IS3AccessKeyStore store,
            IBifrostAuthContextFactory authFactory,
            S3Options options,
            TimeProvider? clock = null,
            ILogger? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? TimeProvider.System;
            _logger = logger;
        }

        /// <summary>
        /// Verifies the request and returns the projected Bifrost user context on success.
        /// Throws <see cref="S3ProtocolException"/> on any auth failure (bad signature, expired
        /// or skewed date, excessive expiry, missing/altered signed material, unknown/disabled
        /// key, or an identity that fails closed on projection).
        /// </summary>
        public async Task<IDictionary<string, object?>> VerifyAsync(HttpRequest request, CancellationToken ct)
        {
            var isPresigned = request.Query.ContainsKey("X-Amz-Algorithm");
            var parsed = isPresigned ? ParsePresigned(request) : ParseHeaderAuth(request);

            ValidateScope(parsed, isPresigned);
            var signTime = ValidateClock(parsed, isPresigned);

            var canonicalHeaders = BuildSignedHeaders(request, parsed, isPresigned);
            var canonicalQuery = BuildCanonicalQuery(request, isPresigned);
            var canonicalUri = S3SigV4.CanonicalUri((request.PathBase + request.Path).ToString());

            var canonicalRequest = S3SigV4.BuildCanonicalRequest(
                request.Method,
                canonicalUri,
                canonicalQuery,
                canonicalHeaders,
                parsed.SignedHeaders,
                parsed.PayloadHash);

            var credentialScope = S3SigV4.CredentialScope(parsed.DateStamp, parsed.Region);
            var stringToSign = S3SigV4.StringToSign(parsed.AmzDate, credentialScope, canonicalRequest);

            var key = await _store.FindAsync(parsed.AccessKeyId, ct);
            var keyUsable = key is { Enabled: true };

            // UNCONDITIONAL constant-time compare: derive a signing key from the real secret
            // when the key is usable, otherwise from a decoy, and compute the expected
            // signature EVERY time. The existence/enabled check is ANDed only AFTER the
            // compare has run, so an unknown key does the same HMAC work as a known one
            // (anti-enumeration; invariant 2).
            var secret = keyUsable ? key!.SecretAccessKey : DecoySecret;
            var signingKey = S3SigV4.DeriveSigningKey(secret, parsed.DateStamp, parsed.Region);
            var expected = S3SigV4.ComputeSignatureHex(signingKey, stringToSign);
            var signatureMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(parsed.Signature));

            if (!(signatureMatches && keyUsable))
                throw S3ProtocolException.SignatureDoesNotMatch();

            return ProjectIdentity(request, key!.Principal);
        }

        /// <summary>
        /// Projects the resolved access key's candidate principal through the shared auth
        /// seam. A subject-less principal, an unmapped OIDC issuer, or a projection that
        /// yields no identity all fail closed as AccessDenied — never a degraded/anonymous
        /// context. No projection detail reaches the wire (logged server-side only).
        /// </summary>
        private IDictionary<string, object?> ProjectIdentity(HttpRequest request, ClaimsPrincipal principal)
        {
            try
            {
                request.HttpContext.User = principal;
                var projected = _authFactory.CreateUserContext(request.HttpContext);
                if (projected.Count == 0)
                {
                    _logger?.LogWarning("S3 access key projected to an empty user context; rejecting.");
                    throw S3ProtocolException.AccessDenied();
                }
                return projected;
            }
            catch (S3ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "S3 identity projection failed; rejecting.");
                throw S3ProtocolException.AccessDenied();
            }
        }

        private ParsedAuth ParseHeaderAuth(HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(header))
                throw S3ProtocolException.AccessDenied("Missing authorization.");

            var space = header.IndexOf(' ');
            if (space < 0 || !header.AsSpan(0, space).SequenceEqual(S3SigV4.Algorithm))
                throw S3ProtocolException.AuthorizationHeaderMalformed();

            string? credential = null, signedHeaders = null, signature = null;
            foreach (var part in header[(space + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var name = part[..eq];
                var value = part[(eq + 1)..];
                switch (name)
                {
                    case "Credential": credential = value; break;
                    case "SignedHeaders": signedHeaders = value; break;
                    case "Signature": signature = value; break;
                }
            }

            if (credential is null || signedHeaders is null || signature is null)
                throw S3ProtocolException.AuthorizationHeaderMalformed();

            var amzDate = request.Headers["x-amz-date"].ToString();
            if (string.IsNullOrEmpty(amzDate))
                throw S3ProtocolException.AuthorizationHeaderMalformed("Missing x-amz-date.");

            var payloadHash = request.Headers["x-amz-content-sha256"].ToString();
            if (string.IsNullOrEmpty(payloadHash))
                throw S3ProtocolException.AuthorizationHeaderMalformed("Missing x-amz-content-sha256.");

            var scope = SplitCredential(credential, malformed: () => S3ProtocolException.AuthorizationHeaderMalformed());
            return new ParsedAuth(scope.AccessKeyId, scope.DateStamp, scope.Region, scope.Service, scope.Terminator,
                NormalizeSignedHeaders(signedHeaders), signature, amzDate, payloadHash, Expires: null);
        }

        private ParsedAuth ParsePresigned(HttpRequest request)
        {
            var q = request.Query;
            if (q["X-Amz-Algorithm"].ToString() != S3SigV4.Algorithm)
                throw S3ProtocolException.AuthorizationQueryParametersError("Unsupported X-Amz-Algorithm.");

            var credential = q["X-Amz-Credential"].ToString();
            var signedHeaders = q["X-Amz-SignedHeaders"].ToString();
            var signature = q["X-Amz-Signature"].ToString();
            var amzDate = q["X-Amz-Date"].ToString();
            var expiresRaw = q["X-Amz-Expires"].ToString();

            if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(signedHeaders)
                || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(amzDate)
                || string.IsNullOrEmpty(expiresRaw))
                throw S3ProtocolException.AuthorizationQueryParametersError();

            // TryParse, never int.Parse: a well-formed but out-of-range value must map to a
            // clean protocol error, not an OverflowException escaping the wire (invariant 5).
            if (!int.TryParse(expiresRaw, out var expires))
                throw S3ProtocolException.AuthorizationQueryParametersError("Invalid X-Amz-Expires.");

            var scope = SplitCredential(credential, malformed: () => S3ProtocolException.AuthorizationQueryParametersError());
            return new ParsedAuth(scope.AccessKeyId, scope.DateStamp, scope.Region, scope.Service, scope.Terminator,
                NormalizeSignedHeaders(signedHeaders), signature, amzDate, S3SigV4.UnsignedPayload, expires);
        }

        private void ValidateScope(ParsedAuth parsed, bool isPresigned)
        {
            if (!string.Equals(parsed.Service, S3SigV4.Service, StringComparison.Ordinal)
                || !string.Equals(parsed.Terminator, S3SigV4.Terminator, StringComparison.Ordinal)
                || !string.Equals(parsed.Region, _options.Region, StringComparison.Ordinal))
                throw Malformed(isPresigned, "Credential scope does not match this endpoint.");

            // The credential scope's date must match the signing timestamp's date.
            if (parsed.AmzDate.Length < 8 || !parsed.AmzDate.AsSpan(0, 8).SequenceEqual(parsed.DateStamp))
                throw Malformed(isPresigned, "Credential scope date does not match X-Amz-Date.");

            if (parsed.SignedHeaders.Length == 0)
                throw Malformed(isPresigned, "Missing signed headers.");

            var signed = new HashSet<string>(parsed.SignedHeaders.Split(';'), StringComparer.Ordinal);
            if (!signed.Contains("host"))
                throw Malformed(isPresigned, "Host must be a signed header.");
            if (!isPresigned)
            {
                foreach (var required in RequiredHeaderAuthSignedHeaders)
                    if (!signed.Contains(required))
                        throw S3ProtocolException.AuthorizationHeaderMalformed($"'{required}' must be a signed header.");
            }
        }

        private DateTimeOffset ValidateClock(ParsedAuth parsed, bool isPresigned)
        {
            if (!DateTimeOffset.TryParseExact(parsed.AmzDate, AmzDateFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var signTime))
                throw Malformed(isPresigned, "Invalid X-Amz-Date.");

            var now = _clock.GetUtcNow();

            if (isPresigned)
            {
                // Excessive declared expiry is rejected before anything else about the
                // presign window (a client asking for a longer-lived URL than policy allows).
                if (parsed.Expires!.Value <= 0
                    || parsed.Expires.Value > (long)_options.MaxPresignedExpiry.TotalSeconds)
                    throw S3ProtocolException.AuthorizationQueryParametersError("X-Amz-Expires is out of range.");

                // A presign is valid from its sign time (minus skew tolerance) until
                // sign time + declared expiry.
                if (now < signTime - _options.MaxClockSkew)
                    throw S3ProtocolException.RequestTimeTooSkewed();
                if (now > signTime.AddSeconds(parsed.Expires.Value))
                    throw S3ProtocolException.RequestExpired();
            }
            else
            {
                if ((now - signTime).Duration() > _options.MaxClockSkew)
                    throw S3ProtocolException.RequestTimeTooSkewed();
            }

            return signTime;
        }

        /// <summary>
        /// Builds the sorted, lower-cased canonical header list for the signed headers,
        /// reading each value from the request. A signed header absent from the request is a
        /// malformed request — the canonical form can't be reconstructed.
        /// </summary>
        private IReadOnlyList<(string Name, string Value)> BuildSignedHeaders(HttpRequest request, ParsedAuth parsed, bool isPresigned)
        {
            var result = new List<(string, string)>();
            foreach (var name in parsed.SignedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string value;
                if (name == "host")
                {
                    value = request.Headers.TryGetValue("Host", out var host) && !StringValues.IsNullOrEmpty(host)
                        ? host.ToString()
                        : request.Host.Value ?? string.Empty;
                }
                else if (request.Headers.TryGetValue(name, out var raw))
                {
                    value = raw.ToString();
                }
                else
                {
                    throw Malformed(isPresigned, "A signed header is not present on the request.");
                }
                result.Add((name, CollapseWhitespace(value)));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return result;
        }

        private static string BuildCanonicalQuery(HttpRequest request, bool isPresigned)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in request.Query)
            {
                // The signature itself is never part of the string it signs.
                if (isPresigned && kv.Key == "X-Amz-Signature")
                    continue;
                foreach (var v in kv.Value)
                    pairs.Add(new KeyValuePair<string, string>(kv.Key, v ?? string.Empty));
            }
            return S3SigV4.CanonicalQueryString(pairs);
        }

        private static CredentialScope SplitCredential(string credential, Func<S3ProtocolException> malformed)
        {
            // "<accessKeyId>/<dateStamp>/<region>/<service>/aws4_request"
            var parts = credential.Split('/');
            if (parts.Length != 5 || parts.Any(string.IsNullOrEmpty))
                throw malformed();
            return new CredentialScope(parts[0], parts[1], parts[2], parts[3], parts[4]);
        }

        private static string NormalizeSignedHeaders(string signedHeaders)
            => signedHeaders.ToLowerInvariant();

        private static string CollapseWhitespace(string value)
        {
            value = value.Trim();
            if (!value.Contains("  ")) return value;
            var sb = new StringBuilder(value.Length);
            var prevSpace = false;
            foreach (var c in value)
            {
                if (c == ' ')
                {
                    if (!prevSpace) sb.Append(c);
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        private static S3ProtocolException Malformed(bool isPresigned, string message)
            => isPresigned
                ? S3ProtocolException.AuthorizationQueryParametersError(message)
                : S3ProtocolException.AuthorizationHeaderMalformed(message);

        private readonly record struct CredentialScope(
            string AccessKeyId, string DateStamp, string Region, string Service, string Terminator);

        private readonly record struct ParsedAuth(
            string AccessKeyId,
            string DateStamp,
            string Region,
            string Service,
            string Terminator,
            string SignedHeaders,
            string Signature,
            string AmzDate,
            string PayloadHash,
            int? Expires);
    }
}
