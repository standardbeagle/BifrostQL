using System.Text;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Paged-results cookie integrity. The cookie carries POSITION ONLY and is not the
    /// authorization boundary — every page is still fetched through the query pipeline, which ANDs
    /// tenant, policy, and soft-delete predicates on unconditionally, so a cookie pointing anywhere
    /// still resolves to at most the caller's own visible rows. What the MAC buys is that a client
    /// cannot tamper with a position, replay a cookie into a different search, or hand one to
    /// another principal — and that none of those failures is distinguishable from any other.
    /// </summary>
    public sealed class LdapPageCookieTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret-not-a-real-key");
        private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        private static LdapFilter AnyEntry() => new LdapFilter.Present("objectClass");

        private static LdapPageBinding Binding(
            string baseObject = "ou=people,dc=example,dc=com",
            int scope = LdapSearchScope.WholeSubtree,
            int pageSize = 50,
            string identity = "IDENTITY-A",
            LdapFilter? filter = null,
            string[]? attributes = null) =>
            new(LdapPageCookie.SearchShapeHash(
                    baseObject, scope, filter ?? AnyEntry(), attributes ?? new[] { "cn" }),
                pageSize, identity);

        [Fact]
        public void Issue_ThenDecode_RecoversThePosition()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(1, 200), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(cookie, Binding(), Secret, Now, Ttl, out var position)
                .Should().BeTrue();

            position.Should().Be(new LdapPagePosition(1, 200));
        }

        [Fact]
        public void Decode_TamperedPayload_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            // Flip a byte in the payload half. The MAC no longer covers it.
            var tampered = (byte[])cookie.Clone();
            tampered[0] ^= 0x01;

            LdapPageCookie.TryDecode(tampered, Binding(), Secret, Now, Ttl, out _).Should().BeFalse();
        }

        [Fact]
        public void Decode_TamperedMac_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            var tampered = (byte[])cookie.Clone();
            tampered[^1] = tampered[^1] == (byte)'A' ? (byte)'B' : (byte)'A';

            LdapPageCookie.TryDecode(tampered, Binding(), Secret, Now, Ttl, out _).Should().BeFalse();
        }

        [Fact]
        public void Decode_ForgedCookieWithAPlausibleShape_IsRejected()
        {
            // A client that guesses the payload format still cannot mint a cookie: it does not have
            // the secret, so the resume position it wanted is unreachable.
            var forged = Encoding.ASCII.GetBytes("MHwwfDE3NTUzODU2MDA.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

            LdapPageCookie.TryDecode(forged, Binding(), Secret, Now, Ttl, out _).Should().BeFalse();
        }

        [Fact]
        public void Decode_UnderADifferentIdentity_IsRejected()
        {
            // Handing a cookie to another principal must not transplant a paging position into
            // their session. The identity fingerprint is folded into the MAC and re-derived live.
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(cookie, Binding(identity: "IDENTITY-B"), Secret, Now, Ttl, out _)
                .Should().BeFalse();
        }

        [Theory]
        [InlineData("uid=alice,ou=people,dc=example,dc=com", LdapSearchScope.WholeSubtree, 50)]
        [InlineData("ou=people,dc=example,dc=com", LdapSearchScope.SingleLevel, 50)]
        [InlineData("ou=people,dc=example,dc=com", LdapSearchScope.WholeSubtree, 25)]
        public void Decode_AgainstADifferentSearchShape_IsRejected(string baseObject, int scope, int pageSize)
        {
            // A client must not be able to page a cheap search and swap in a different one
            // mid-sequence: base object, scope, and page size all bind the cookie.
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(
                    cookie, Binding(baseObject: baseObject, scope: scope, pageSize: pageSize),
                    Secret, Now, Ttl, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void Decode_AgainstADifferentFilter_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            var otherFilter = new LdapFilter.Comparison(
                LdapProtocol.FilterEqualityMatch, "cn", Encoding.UTF8.GetBytes("alice"));

            LdapPageCookie.TryDecode(cookie, Binding(filter: otherFilter), Secret, Now, Ttl, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void Decode_AgainstADifferentAttributeSelection_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(
                    cookie, Binding(attributes: new[] { "cn", "mail" }), Secret, Now, Ttl, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void Decode_UnderADifferentSecret_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(
                    cookie, Binding(), Encoding.UTF8.GetBytes("a-different-secret"), Now, Ttl, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void Decode_AnExpiredCookie_IsRejected()
        {
            // Authentic but stale fails exactly like forged: same outcome, so there is no oracle
            // separating "this was once valid" from "this was never valid".
            var cookie = LdapPageCookie.Issue(new LdapPagePosition(0, 10), Now, Binding(), Secret);

            LdapPageCookie.TryDecode(cookie, Binding(), Secret, Now + Ttl + TimeSpan.FromSeconds(1), Ttl, out _)
                .Should().BeFalse();
        }

        [Fact]
        public void Decode_ACookieFromTheFuture_IsRejected()
        {
            var cookie = LdapPageCookie.Issue(
                new LdapPagePosition(0, 10), Now + TimeSpan.FromHours(1), Binding(), Secret);

            LdapPageCookie.TryDecode(cookie, Binding(), Secret, Now, Ttl, out _).Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-dot-separator")]
        [InlineData(".")]
        [InlineData("payload.")]
        [InlineData(".mac")]
        [InlineData("!!!not-base64!!!.###")]
        [InlineData("AAAAA.AAAAA")]
        public void Decode_MalformedCookie_IsRejectedWithoutThrowing(string cookie)
        {
            // Malformed wire octets reach this from an authenticated but hostile peer. They must
            // become a clean refusal, never an exception escaping into the connection loop.
            var act = () => LdapPageCookie.TryDecode(
                Encoding.ASCII.GetBytes(cookie), Binding(), Secret, Now, Ttl, out _);

            act.Should().NotThrow();
            act().Should().BeFalse();
        }

        [Fact]
        public void Decode_OversizedNumericPayload_IsRejectedWithoutThrowing()
        {
            // A 29-digit offset is well-formed text but out of int range. The throwing parse
            // overloads raise OverflowException, not FormatException -- a catch scoped to the
            // obviously-malformed case would let this escape (invariant 5). Minted with the real
            // secret so the MAC passes and the parse is genuinely reached.
            var oversized = new string('9', 29);
            var payload = $"0|{oversized}|{Now.ToUnixTimeSeconds()}";
            var cookie = ForgeWithValidMac(payload);

            var act = () => LdapPageCookie.TryDecode(cookie, Binding(), Secret, Now, Ttl, out _);

            act.Should().NotThrow();
            act().Should().BeFalse();
        }

        [Fact]
        public void Decode_NegativePosition_IsRejected()
        {
            // A negative offset would flow into a query's OFFSET. It never reaches one.
            var cookie = ForgeWithValidMac($"0|-5|{Now.ToUnixTimeSeconds()}");

            LdapPageCookie.TryDecode(cookie, Binding(), Secret, Now, Ttl, out _).Should().BeFalse();
        }

        [Fact]
        public void SearchShapeHash_IsInjectiveAcrossFieldBoundaries()
        {
            // Without length prefixing, ("ab", ["c"]) and ("a", ["bc"]) could concatenate to the
            // same bytes and share a hash -- letting a cookie replay into a different search.
            var first = LdapPageCookie.SearchShapeHash("ab", 2, AnyEntry(), new[] { "c" });
            var second = LdapPageCookie.SearchShapeHash("a", 2, AnyEntry(), new[] { "bc" });

            first.Should().NotBe(second);
        }

        [Fact]
        public void FingerprintIdentity_IsStableAcrossOrderingAndDiffersByPrincipal()
        {
            // Stability matters as much as separation: an unstable fingerprint would reject a
            // principal's own valid cookies on the next request.
            var a = LdapPageCookie.FingerprintIdentity(new Dictionary<string, object?>
            {
                ["sub"] = "alice",
                ["roles"] = new[] { "staff", "admin" },
            });
            var reordered = LdapPageCookie.FingerprintIdentity(new Dictionary<string, object?>
            {
                ["roles"] = new[] { "staff", "admin" },
                ["sub"] = "alice",
            });
            var other = LdapPageCookie.FingerprintIdentity(new Dictionary<string, object?>
            {
                ["sub"] = "bob",
                ["roles"] = new[] { "staff", "admin" },
            });

            a.Should().Be(reordered);
            a.Should().NotBe(other);
        }

        [Fact]
        public void FingerprintIdentity_IgnoresOpaqueEntries()
        {
            // A non-scalar entry (a live service object, say) would differ between requests and
            // make every cookie fail for its own issuer.
            var withOpaque = LdapPageCookie.FingerprintIdentity(new Dictionary<string, object?>
            {
                ["sub"] = "alice",
                ["opaque"] = new object(),
            });
            var without = LdapPageCookie.FingerprintIdentity(new Dictionary<string, object?>
            {
                ["sub"] = "alice",
            });

            withOpaque.Should().Be(without);
        }

        // Mints a cookie with a genuine MAC over an arbitrary payload, so a test can drive the
        // PAYLOAD PARSE rather than being stopped at the integrity gate.
        private static byte[] ForgeWithValidMac(string payload)
        {
            var binding = Binding();
            var canonical = new StringBuilder()
                .Append("ldappage1").Append('\n')
                .Append(binding.SearchShapeHash.Length).Append(':').Append(binding.SearchShapeHash).Append('\n')
                .Append(binding.PageSize).Append('\n')
                .Append(binding.IdentityFingerprint.Length).Append(':').Append(binding.IdentityFingerprint).Append('\n')
                .Append(payload.Length).Append(':').Append(payload)
                .ToString();

            var mac = System.Security.Cryptography.HMACSHA256.HashData(
                Secret, Encoding.UTF8.GetBytes(canonical));

            return Encoding.ASCII.GetBytes(
                Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(mac));

            static string Base64Url(byte[] bytes) =>
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
