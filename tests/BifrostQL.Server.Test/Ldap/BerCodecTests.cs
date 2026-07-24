using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Low-level BER TLV codec tests: the <see cref="BerWriter"/>/<see cref="BerCursor"/> pair must
    /// round-trip every primitive the LDAP codec builds on, and every malformed/oversized encoding
    /// must raise a clean <see cref="LdapProtocolException"/> rather than an unhandled BCL throw or
    /// an out-of-bounds read — the framing-robustness guarantee the connection loop relies on to
    /// close predictably on hostile input.
    /// </summary>
    public sealed class BerCodecTests
    {
        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(-1L)]
        [InlineData(127L)]
        [InlineData(128L)]        // needs a leading 0x00 so the sign stays positive
        [InlineData(-128L)]
        [InlineData(255L)]
        [InlineData(256L)]
        [InlineData(-129L)]
        [InlineData(65535L)]
        [InlineData(4294967295L)] // > int.MaxValue: a valid 64-bit LDAP integer
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void Integer_RoundTrips_PreservingSign(long value)
        {
            // Arrange: encode with the writer's minimal two's-complement form.
            var encoded = BerWriter.Integer(value);

            // Act: decode it back through the cursor.
            var cursor = new BerCursor(encoded, 0, encoded.Length);
            var decoded = cursor.Integer(cursor.ReadElement(LdapProtocol.Integer));

            // Assert
            decoded.Should().Be(value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("dc=example,dc=com")]
        [InlineData("a value with spaces and Ünïcödé")]
        public void OctetString_RoundTrips(string value)
        {
            var encoded = BerWriter.OctetString(value);
            var cursor = new BerCursor(encoded, 0, encoded.Length);
            cursor.String(cursor.ReadElement(LdapProtocol.OctetString)).Should().Be(value);
        }

        [Fact]
        public void Sequence_OfMixedElements_RoundTrips()
        {
            // Arrange: a nested SEQUENCE { INTEGER, OCTET STRING } — the shape every LDAP op reuses.
            var encoded = BerWriter.Sequence(BerWriter.Integer(42), BerWriter.OctetString("cn=admin"));

            // Act
            var outer = new BerCursor(encoded, 0, encoded.Length);
            var seq = outer.Child(outer.ReadElement(LdapProtocol.Sequence));
            var number = seq.Int32(seq.ReadElement(LdapProtocol.Integer));
            var text = seq.String(seq.ReadElement(LdapProtocol.OctetString));

            // Assert
            number.Should().Be(42);
            text.Should().Be("cn=admin");
        }

        [Fact]
        public void LongFormLength_RoundTrips()
        {
            // Arrange: a 200-byte octet string forces the long-form length encoding (0x81 0xC8).
            var payload = new string('x', 200);
            var encoded = BerWriter.OctetString(payload);

            // Act
            var cursor = new BerCursor(encoded, 0, encoded.Length);
            var decoded = cursor.String(cursor.ReadElement(LdapProtocol.OctetString));

            // Assert
            decoded.Should().Be(payload);
        }

        [Fact]
        public void IndefiniteLength_IsRejected()
        {
            // Arrange: 0x80 length is the forbidden indefinite form (LDAP mandates definite length).
            var bytes = new byte[] { LdapProtocol.OctetString, 0x80 };
            var cursor = new BerCursor(bytes, 0, bytes.Length);

            // Act / Assert
            var act = () => cursor.ReadElement();
            act.Should().Throw<LdapProtocolException>().WithMessage("*indefinite*");
        }

        [Fact]
        public void DefiniteLength_OverrunningContainer_IsRejected()
        {
            // Arrange: declares 5 content bytes but only 2 are present.
            var bytes = new byte[] { LdapProtocol.OctetString, 0x05, 0x01, 0x02 };
            var cursor = new BerCursor(bytes, 0, bytes.Length);

            // Act / Assert
            var act = () => cursor.ReadElement();
            act.Should().Throw<LdapProtocolException>().WithMessage("*overruns*");
        }

        [Fact]
        public void OversizedInteger_BeyondInt64_IsRejected()
        {
            // Arrange: a 9-byte integer cannot fit a 64-bit value — a boundary value that must be a
            // clean protocol error, not a silent truncation (protocol-adapter-security invariant 5).
            var bytes = new byte[] { LdapProtocol.Integer, 0x09, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var cursor = new BerCursor(bytes, 0, bytes.Length);

            // Act / Assert
            var act = () => cursor.Integer(cursor.ReadElement());
            act.Should().Throw<LdapProtocolException>().WithMessage("*64-bit*");
        }

        [Fact]
        public void OverWideLengthPrefix_IsRejected()
        {
            // Arrange: 0x85 announces a 5-byte length prefix — past the 32-bit bound.
            var bytes = new byte[] { LdapProtocol.OctetString, 0x85, 0, 0, 0, 0, 0 };
            var cursor = new BerCursor(bytes, 0, bytes.Length);

            // Act / Assert
            var act = () => cursor.ReadElement();
            act.Should().Throw<LdapProtocolException>();
        }

        [Fact]
        public void TruncatedTag_IsRejected()
        {
            // Arrange: an empty buffer has no tag to read.
            var cursor = new BerCursor(Array.Empty<byte>(), 0, 0);

            // Act / Assert
            var act = () => cursor.ReadElement();
            act.Should().Throw<LdapProtocolException>();
        }
    }
}
