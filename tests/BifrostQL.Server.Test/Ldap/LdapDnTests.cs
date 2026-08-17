using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Distinguished-name syntax (RFC 4514). An entry's DN is built from a COLUMN VALUE, so the
    /// escaping rules are what keep the DN a faithful, injective rendering of that value: without
    /// them a value carrying a comma splits into extra DN components, and two different rows can
    /// produce the same DN text. The comparison rules matter for the opposite reason — a client may
    /// spell a DN in any equivalent way, and all of them must resolve to the same entry.
    /// </summary>
    public sealed class LdapDnTests
    {
        [Theory]
        [InlineData("Doe, John", "Doe\\, John")]
        [InlineData("a+b", "a\\+b")]
        [InlineData("back\\slash", "back\\\\slash")]
        [InlineData("quote\"here", "quote\\\"here")]
        [InlineData("semi;colon", "semi\\;colon")]
        [InlineData("<angle>", "\\<angle\\>")]
        [InlineData("eq=uals", "eq\\=uals")]
        [InlineData("#leading", "\\#leading")]
        [InlineData(" leading", "\\ leading")]
        [InlineData("trailing ", "trailing\\ ")]
        [InlineData("plain", "plain")]
        public void Escape_EscapesEverySpecialPosition(string value, string expected)
        {
            LdapDn.Escape(value).Should().Be(expected);
        }

        [Theory]
        [InlineData("Doe, John")]
        [InlineData("a+b")]
        [InlineData("back\\slash")]
        [InlineData("#hash")]
        [InlineData(" padded ")]
        [InlineData("plain")]
        public void EscapeThenParse_RoundTripsTheOriginalValue(string value)
        {
            // The round trip is the whole point: an entry named by a column value must resolve back
            // to exactly that value when a client addresses the entry by its DN.
            var dn = $"{LdapDn.FormatRdn("cn", value)},ou=people,dc=example,dc=com";

            LdapDn.TryParse(dn, out var components).Should().BeTrue();

            components[0].Attribute.Should().Be("cn");
            components[0].Value.Should().Be(value);
            components.Should().HaveCount(4, "the escaped value must not split into extra components");
        }

        [Fact]
        public void Parse_ValueWithEscapedComma_DoesNotSplitIntoAnExtraComponent()
        {
            // The failure this prevents: unescaped, "Doe, John" would name an entry whose parent
            // container is "John" — a different place in the tree than the one it belongs to.
            LdapDn.TryParse("cn=Doe\\, John,ou=people", out var components).Should().BeTrue();

            components.Should().HaveCount(2);
            components[0].Value.Should().Be("Doe, John");
            components[1].Attribute.Should().Be("ou");
        }

        [Fact]
        public void Parse_HexEscape_Decodes()
        {
            LdapDn.TryParse("cn=a\\2Ab", out var components).Should().BeTrue();
            components[0].Value.Should().Be("a*b");
        }

        [Theory]
        [InlineData("cn=dangling\\")]      // escape with nothing after it
        [InlineData("cn=half\\2")]         // half a hex escape
        [InlineData("novalue")]            // no '=' at all
        [InlineData("=novalue")]           // no attribute type
        [InlineData("cn=a,,ou=b")]         // empty component
        [InlineData("1cn=a")]              // attribute type must start with a letter
        public void Parse_MalformedDn_IsRejected(string dn)
        {
            LdapDn.TryParse(dn, out _).Should().BeFalse();
        }

        [Fact]
        public void CanonicalKey_IsCaseAndWhitespaceInsensitive()
        {
            // A client may spell the same DN any of these ways; all must address one entry.
            var a = LdapDn.CanonicalKey("UID=Alice,OU=People,DC=Example,DC=Com");
            var b = LdapDn.CanonicalKey("uid=alice, ou=people, dc=example, dc=com");

            a.Should().NotBeNull().And.Be(b);
        }

        [Fact]
        public void CanonicalKey_OfAMalformedDn_IsNull()
        {
            // An unparseable DN is never equal to anything, including itself — so it can never be
            // accidentally matched against a real entry's key.
            LdapDn.CanonicalKey("cn=dangling\\").Should().BeNull();
        }

        [Fact]
        public void IsDescendantOf_MatchesOnAComponentBoundary_NotATextSuffix()
        {
            var ancestor = LdapDn.CanonicalKey("dc=example,dc=com")!;

            LdapDn.IsDescendantOf(LdapDn.CanonicalKey("ou=people,dc=example,dc=com")!, ancestor)
                .Should().BeTrue();

            // A raw text-suffix test would call this a descendant: it ends with "dc=example,dc=com"
            // but the component immediately above is "dc=notexample", a different subtree entirely.
            // That is precisely how a suffix check becomes a scope escape.
            LdapDn.IsDescendantOf(LdapDn.CanonicalKey("ou=people,dc=notexample,dc=com")!, ancestor)
                .Should().BeFalse();

            // A DN is not a descendant of itself.
            LdapDn.IsDescendantOf(ancestor, ancestor).Should().BeFalse();
        }

        [Fact]
        public void IsChildOf_RequiresExactlyOneLevel()
        {
            var container = LdapDn.CanonicalKey("ou=people,dc=example,dc=com")!;

            LdapDn.IsChildOf(LdapDn.CanonicalKey("uid=alice,ou=people,dc=example,dc=com")!, container)
                .Should().BeTrue();
            LdapDn.IsChildOf(LdapDn.CanonicalKey("uid=alice,ou=staff,ou=people,dc=example,dc=com")!, container)
                .Should().BeFalse();
        }

        [Fact]
        public void IsChildOf_ValueContainingAnEscapedComma_IsStillOneLevel()
        {
            // The depth test counts UNESCAPED separators. Counting raw commas would read this
            // single-component RDN as two levels and drop the entry out of a one-level search.
            var container = LdapDn.CanonicalKey("ou=people,dc=example,dc=com")!;
            var entry = LdapDn.CanonicalKey("cn=Doe\\, John,ou=people,dc=example,dc=com")!;

            LdapDn.IsChildOf(entry, container).Should().BeTrue();
        }
    }
}
