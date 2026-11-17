using BifrostQL.Server;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// The shared fingerprint every continuation token and per-caller cache key uses. The per-door
    /// copies it replaced joined sequence elements with "," and values with "=", so distinct
    /// identities could render to the same canonical text.
    /// </summary>
    public sealed class ProtocolIdentityFingerprintTests
    {
        public static TheoryData<IDictionary<string, object?>, IDictionary<string, object?>> DistinctIdentities() => new()
        {
            // A role list ["a","b"] vs ["a,b"]: the old "," join rendered both "[a,b]".
            { Ctx("roles", new[] { "a", "b" }), Ctx("roles", new[] { "a,b" }) },
            // A list vs a string that looks like its rendering.
            { Ctx("roles", new[] { "a", "b" }), Ctx("roles", "[a,b]") },
            // A key containing "=" vs a value containing it.
            { Ctx("a=b", "c"), Ctx("a", "b=c") },
            // A number vs its string form.
            { Ctx("tenant", 1), Ctx("tenant", "1") },
            // Non-string sequences render their elements, never the type name.
            { Ctx("ids", new[] { 1, 2 }), Ctx("ids", new[] { 3 }) },
        };

        [Theory]
        [MemberData(nameof(DistinctIdentities))]
        public void Distinct_identities_get_distinct_fingerprints(
            IDictionary<string, object?> left, IDictionary<string, object?> right)
            => ProtocolIdentityFingerprint.Of(left).Should().NotBe(ProtocolIdentityFingerprint.Of(right));

        [Fact]
        public void Same_identity_in_any_entry_order_gets_the_same_fingerprint_and_opaque_entries_are_ignored()
        {
            var first = new Dictionary<string, object?> { ["sub"] = "u1", ["roles"] = new[] { "x" }, ["user"] = new object() };
            var second = new Dictionary<string, object?> { ["user"] = new object(), ["roles"] = new List<string> { "x" }, ["sub"] = "u1" };

            ProtocolIdentityFingerprint.Of(first).Should().Be(ProtocolIdentityFingerprint.Of(second));
        }

        [Fact]
        public void Anonymous_differs_from_an_empty_context()
            => ProtocolIdentityFingerprint.Of(null).Should().NotBe(ProtocolIdentityFingerprint.Of(new Dictionary<string, object?>()));

        private static IDictionary<string, object?> Ctx(string key, object value)
            => new Dictionary<string, object?> { [key] = value };
    }
}
