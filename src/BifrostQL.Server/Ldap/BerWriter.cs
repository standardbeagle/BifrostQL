using System.Text;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Builds BER (definite-length) TLV byte arrays — the exact inverse of <see cref="BerCursor"/>,
    /// so any element the reader decodes re-encodes to the same bytes. Values are composed
    /// bottom-up: a leaf encodes its content, a constructed element concatenates its children and
    /// prefixes the tag + definite length. Lengths are always known before they are written
    /// (content is fully materialized first), which is why LDAP never needs the indefinite form.
    /// </summary>
    internal static class BerWriter
    {
        /// <summary>A tag-length-value element wrapping already-encoded content bytes.</summary>
        public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
        {
            var length = EncodeLength(content.Length);
            var result = new byte[1 + length.Length + content.Length];
            result[0] = tag;
            length.CopyTo(result.AsSpan(1));
            content.CopyTo(result.AsSpan(1 + length.Length));
            return result;
        }

        /// <summary>A constructed element (SEQUENCE / SET / context-tagged) over concatenated children.</summary>
        public static byte[] Constructed(byte tag, params byte[][] children) => Tlv(tag, Concat(children));

        /// <summary>A SEQUENCE (<c>0x30</c>) over concatenated children.</summary>
        public static byte[] Sequence(params byte[][] children) => Constructed(LdapProtocol.Sequence, children);

        /// <summary>A SET (<c>0x31</c>) over concatenated children.</summary>
        public static byte[] Set(params byte[][] children) => Constructed(LdapProtocol.Set, children);

        /// <summary>An OCTET STRING (<c>0x04</c>) of UTF-8 text.</summary>
        public static byte[] OctetString(string value) => Tlv(LdapProtocol.OctetString, Encoding.UTF8.GetBytes(value));

        /// <summary>A context/application-tagged primitive octet string (e.g. an OID request/response name).</summary>
        public static byte[] TaggedString(byte tag, string value) => Tlv(tag, Encoding.UTF8.GetBytes(value));

        /// <summary>An INTEGER (<c>0x02</c>) in minimal two's-complement form.</summary>
        public static byte[] Integer(long value) => Tlv(LdapProtocol.Integer, MinimalIntegerBytes(value));

        /// <summary>An ENUMERATED (<c>0x0A</c>) — the encoding of an LDAP result code / scope.</summary>
        public static byte[] Enumerated(long value) => Tlv(LdapProtocol.Enumerated, MinimalIntegerBytes(value));

        /// <summary>Concatenates encoded parts into one buffer.</summary>
        public static byte[] Concat(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts)
                total += part.Length;
            var result = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }

        /// <summary>Encodes a length in short form (&lt;128) or minimal long form.</summary>
        public static byte[] EncodeLength(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (length < 0x80)
                return new[] { (byte)length };

            Span<byte> big = stackalloc byte[4];
            var count = 0;
            var value = (uint)length;
            // Big-endian minimal bytes.
            for (var shift = 24; shift >= 0; shift -= 8)
            {
                var b = (byte)(value >> shift);
                if (count == 0 && b == 0)
                    continue;
                big[count++] = b;
            }
            var result = new byte[1 + count];
            result[0] = (byte)(0x80 | count);
            big[..count].CopyTo(result.AsSpan(1));
            return result;
        }

        // BER integer: minimal-length two's-complement, big-endian. A leading byte is dropped only
        // when it is redundant with the sign of the next byte (0x00 before a clear high bit, 0xFF
        // before a set high bit), so the sign always round-trips.
        private static byte[] MinimalIntegerBytes(long value)
        {
            Span<byte> full = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
                full[i] = (byte)(value >> (56 - i * 8));

            var start = 0;
            while (start < 7 &&
                   ((full[start] == 0x00 && (full[start + 1] & 0x80) == 0) ||
                    (full[start] == 0xFF && (full[start + 1] & 0x80) != 0)))
            {
                start++;
            }
            return full[start..].ToArray();
        }
    }
}
