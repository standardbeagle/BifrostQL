using BifrostQL.Server.S3;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.S3
{
    /// <summary>
    /// Exercises the SigV4 auth core against every acceptance-criterion reject case for both
    /// the header-authorization and presigned-GET paths, plus the fail-closed identity
    /// projection. Requests are built by <see cref="S3TestSigner"/> from the same
    /// canonicalization the verifier uses, so a rejection is a real signature/scope/clock
    /// failure and not signer drift.
    /// </summary>
    public sealed class S3SigV4VerifierTests
    {
        private static readonly DateTimeOffset SignTime = new(2026, 07, 16, 12, 00, 00, TimeSpan.Zero);

        private sealed class TestClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static S3SigV4Verifier Verifier(
            IS3AccessKeyStore store,
            DateTimeOffset? now = null,
            string region = S3TestSigner.Region,
            S3Options? options = null)
        {
            var opts = options ?? new S3Options { Region = region };
            return new S3SigV4Verifier(store, BifrostAuthContextFactory.Instance, opts, new TestClock(now ?? SignTime));
        }

        private static FakeS3AccessKeyStore StoreWith(bool enabled = true, string subject = "s3-user", string? tenant = null)
            => new FakeS3AccessKeyStore().Add(S3TestSigner.AccessKeyId, S3TestSigner.Secret,
                S3TestSigner.Principal(subject, tenant), enabled);

        [Fact]
        public async Task Header_valid_signature_projects_identity()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime, path: "/bucket/report.csv");
            var verifier = Verifier(StoreWith(tenant: "tenant-a"));

            var userContext = await verifier.VerifyAsync(ctx.Request, CancellationToken.None);

            userContext.Should().NotBeEmpty("a verified request must yield a projected identity");
            userContext.Should().ContainKey("user");
        }

        [Fact]
        public async Task Presigned_get_valid_signature_projects_identity()
        {
            var ctx = S3TestSigner.BuildPresigned(signTime: SignTime, expires: 3600);
            var verifier = Verifier(StoreWith());

            var userContext = await verifier.VerifyAsync(ctx.Request, CancellationToken.None);

            userContext.Should().NotBeEmpty();
        }

        [Fact]
        public async Task Header_bad_signature_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var auth = ctx.Request.Headers["Authorization"].ToString();
            ctx.Request.Headers["Authorization"] = auth[..^8] + "deadbeef"; // corrupt trailing signature

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        [Fact]
        public async Task Unknown_access_key_is_rejected_as_signature_mismatch()
        {
            // Anti-enumeration: an unknown key fails the SAME way (code + status) as a wrong
            // signature, because the compare runs unconditionally against a decoy secret.
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var verifier = Verifier(new FakeS3AccessKeyStore()); // no keys

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch" && e.HttpStatus == 403);
        }

        [Fact]
        public async Task Disabled_access_key_is_rejected_indistinguishably()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var verifier = Verifier(StoreWith(enabled: false));

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        [Fact]
        public async Task Header_expired_date_beyond_skew_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var verifier = Verifier(StoreWith(), now: SignTime.AddMinutes(20)); // > 15 min skew

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "RequestTimeTooSkewed");
        }

        [Fact]
        public async Task Presigned_excessive_expiry_is_rejected_before_signature()
        {
            var eightDays = (int)TimeSpan.FromDays(8).TotalSeconds;
            var ctx = S3TestSigner.BuildPresigned(signTime: SignTime, expires: eightDays);
            var verifier = Verifier(StoreWith());

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AuthorizationQueryParametersError");
        }

        [Fact]
        public async Task Presigned_past_its_expiry_window_is_rejected()
        {
            var ctx = S3TestSigner.BuildPresigned(signTime: SignTime, expires: 60);
            var verifier = Verifier(StoreWith(), now: SignTime.AddMinutes(2)); // past sign+expiry

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AccessDenied");
        }

        [Fact]
        public async Task Missing_host_signed_header_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            var auth = ctx.Request.Headers["Authorization"].ToString();
            ctx.Request.Headers["Authorization"] =
                auth.Replace("SignedHeaders=host;x-amz-content-sha256;x-amz-date",
                             "SignedHeaders=x-amz-content-sha256;x-amz-date");

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AuthorizationHeaderMalformed");
        }

        [Fact]
        public async Task Altered_path_after_signing_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime, path: "/bucket/original.txt");
            ctx.Request.Path = "/bucket/tampered.txt";

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        [Fact]
        public async Task Altered_query_after_signing_is_rejected()
        {
            var ctx = S3TestSigner.BuildPresigned(signTime: SignTime);
            // Append an unsigned parameter — canonical query now differs from what was signed.
            ctx.Request.QueryString = ctx.Request.QueryString.Add("injected", "1");

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        [Fact]
        public async Task Altered_payload_hash_after_signing_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            ctx.Request.Headers["x-amz-content-sha256"] = new string('a', 64); // different hash

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        [Fact]
        public async Task Wrong_region_scope_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime); // signed us-east-1
            var verifier = Verifier(StoreWith(), region: "us-west-2");     // endpoint expects us-west-2

            await verifier.Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AuthorizationHeaderMalformed");
        }

        [Fact]
        public async Task Malformed_authorization_header_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            ctx.Request.Headers["Authorization"] = "not-a-sigv4-header";

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AuthorizationHeaderMalformed");
        }

        [Fact]
        public async Task Missing_authorization_is_rejected()
        {
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);
            ctx.Request.Headers.Remove("Authorization");

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AccessDenied");
        }

        [Fact]
        public async Task Subjectless_identity_fails_closed_after_valid_signature()
        {
            // A correct signature that maps to a principal with no subject claim must NOT
            // degrade to an anonymous context — it fails closed as AccessDenied.
            var store = new FakeS3AccessKeyStore().Add(
                S3TestSigner.AccessKeyId, S3TestSigner.Secret, S3TestSigner.SubjectlessPrincipal());
            var ctx = S3TestSigner.BuildHeaderSigned(signTime: SignTime);

            await Verifier(store).Invoking(v => v.VerifyAsync(ctx.Request, CancellationToken.None))
                .Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "AccessDenied");
        }

        [Fact]
        public async Task Presigned_bad_signature_is_rejected()
        {
            var ctx = S3TestSigner.BuildPresigned(signTime: SignTime);
            ctx.Request.QueryString = QueryString.Empty;
            foreach (var kv in S3TestSigner.BuildPresigned(signTime: SignTime).Request.Query)
            {
                var value = kv.Key == "X-Amz-Signature" ? new string('0', 64) : kv.Value.ToString();
                ctx.Request.QueryString = ctx.Request.QueryString.Add(kv.Key, value);
            }

            await Verify(ctx).Should().ThrowAsync<S3ProtocolException>()
                .Where(e => e.Code == "SignatureDoesNotMatch");
        }

        private Func<Task> Verify(DefaultHttpContext ctx)
        {
            var verifier = Verifier(StoreWith());
            return () => verifier.VerifyAsync(ctx.Request, CancellationToken.None);
        }
    }
}
