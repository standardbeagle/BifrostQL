using System.Text;
using BifrostQL.Server.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Unit proofs for the opaque, integrity-protected server-driven-paging token: a token
    /// round-trips its offset only when replayed against the identical binding it was minted
    /// for, and every tamper / cross-context / expiry / malformed case fails DETERMINISTICALLY
    /// as a clean OData 400 — never a silent wrong-page, never an unhandled fault. This is the
    /// security core of slice 5 (criteria 3 and 4).
    /// </summary>
    public sealed class ODataContinuationTokenTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("odata-continuation-secret-key-0001");
        private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

        private static ODataPageBinding Binding(
            string set = "Widgets", string? filter = null, string? select = null, string? orderBy = null,
            int pageSize = 50, string identity = "id-a")
            => new(set, ODataContinuationToken.QueryShapeHash(filter, select, orderBy), pageSize, identity);

        [Fact]
        public void Round_trips_the_offset_under_the_identical_binding()
        {
            var binding = Binding();
            var token = ODataContinuationToken.Issue(50, T0, binding, Secret);

            var offset = ODataContinuationToken.Decode(token, binding, Secret, T0.AddMinutes(1), Ttl);

            offset.Should().Be(50);
        }

        [Fact]
        public void A_flipped_byte_fails_as_a_clean_400()
        {
            var binding = Binding();
            var token = ODataContinuationToken.Issue(50, T0, binding, Secret);
            var tampered = FlipMacByte(token);

            var act = () => ODataContinuationToken.Decode(tampered, binding, Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // Flips the FIRST base64url character of the MAC segment (6 significant bits → always a real
        // byte change), so the tamper is deterministic rather than depending on the run's MAC bytes
        // (a last-char flip can be a no-op under base64 padding).
        internal static string FlipMacByte(string token)
        {
            var dot = token.IndexOf('.');
            var chars = token.ToCharArray();
            var i = dot + 1;
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            return new string(chars);
        }

        [Fact]
        public void A_token_minted_for_set_A_is_rejected_on_set_B()
        {
            var token = ODataContinuationToken.Issue(50, T0, Binding(set: "Widgets"), Secret);

            var act = () => ODataContinuationToken.Decode(token, Binding(set: "Orders"), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void A_token_minted_for_one_filter_is_rejected_with_a_different_filter()
        {
            var token = ODataContinuationToken.Issue(50, T0, Binding(filter: "price gt 5"), Secret);

            var act = () => ODataContinuationToken.Decode(token, Binding(filter: "price gt 6"), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void A_token_minted_for_one_orderby_is_rejected_with_a_different_orderby()
        {
            var token = ODataContinuationToken.Issue(50, T0, Binding(orderBy: "name asc"), Secret);

            var act = () => ODataContinuationToken.Decode(token, Binding(orderBy: "name desc"), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void A_token_minted_at_one_page_size_is_rejected_at_another()
        {
            var token = ODataContinuationToken.Issue(50, T0, Binding(pageSize: 50), Secret);

            var act = () => ODataContinuationToken.Decode(token, Binding(pageSize: 25), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void A_token_minted_by_identity_A_is_rejected_when_replayed_by_identity_B()
        {
            var token = ODataContinuationToken.Issue(50, T0, Binding(identity: "id-a"), Secret);

            var act = () => ODataContinuationToken.Decode(token, Binding(identity: "id-b"), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void An_expired_token_fails_as_a_clean_400()
        {
            var binding = Binding();
            var token = ODataContinuationToken.Issue(50, T0, binding, Secret);

            var act = () => ODataContinuationToken.Decode(token, binding, Secret, T0 + Ttl + TimeSpan.FromSeconds(1), Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void A_token_signed_with_another_secret_is_rejected()
        {
            var binding = Binding();
            var token = ODataContinuationToken.Issue(50, T0, binding, Secret);
            var otherSecret = Encoding.UTF8.GetBytes("a-completely-different-secret-key0");

            var act = () => ODataContinuationToken.Decode(token, binding, otherSecret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-token")]
        [InlineData(".onlymac")]
        [InlineData("onlypayload.")]
        [InlineData("!!!not-base64!!!.@@@")]
        public void Malformed_tokens_are_clean_400s_never_an_unhandled_fault(string token)
        {
            var act = () => ODataContinuationToken.Decode(token, Binding(), Secret, T0, Ttl);

            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void The_token_payload_does_not_leak_binding_or_identity_in_cleartext()
        {
            var binding = Binding(set: "SecretWidgets", filter: "tenant_id eq 'tenant-a'", identity: "user-a-fingerprint");
            var token = ODataContinuationToken.Issue(50, T0, binding, Secret);

            // The transmitted payload segment (before the dot) decodes to offset|issuedAt only —
            // no entity-set name, no filter text, no identity plaintext (criterion 4).
            var payloadSegment = token[..token.IndexOf('.')];
            var padded = payloadSegment.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(padded));

            payload.Should().NotContain("SecretWidgets");
            payload.Should().NotContain("tenant-a");
            payload.Should().NotContain("user-a-fingerprint");
            payload.Should().Be("50|" + T0.ToUnixTimeSeconds());
        }
    }
}
