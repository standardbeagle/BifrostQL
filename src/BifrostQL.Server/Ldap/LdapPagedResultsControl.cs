namespace BifrostQL.Server.Ldap
{
    /// <summary>A decoded simple paged-results request control: the client's page size and its cookie.</summary>
    internal sealed record LdapPagedResultsRequest(int Size, byte[] Cookie);

    /// <summary>
    /// The simple paged-results control (RFC 2696), the only request control this front door
    /// implements. Its value is
    /// <c>SEQUENCE { size INTEGER, cookie OCTET STRING }</c>.
    ///
    /// <para>Decoding runs on adversary-controlled octets, so it uses the same bounded
    /// <see cref="BerCursor"/> the message codec uses and raises the adapter's own
    /// <see cref="LdapProtocolException"/> — already in the connection loop's caught family, so a
    /// malformed control closes cleanly instead of escaping to the host
    /// (protocol-adapter-security invariant 1).</para>
    ///
    /// <para>A negative page size is refused rather than clamped: it would otherwise flow into a
    /// query's row limit, and a limit whose value cannot be interpreted must not be guessed at.</para>
    /// </summary>
    internal static class LdapPagedResultsControl
    {
        /// <summary>The control OID clients send to request server-side paging.</summary>
        public const string Oid = "1.2.840.113556.1.4.319";

        /// <summary>
        /// Whether an OID is a control this server implements. Everything else is unsupported —
        /// and if the client marked it CRITICAL, RFC 4511 §4.1.11 requires refusing the whole
        /// operation rather than silently ignoring it, because the client has declared the
        /// operation meaningless without it.
        /// </summary>
        public static bool IsSupported(string oid) =>
            string.Equals(oid, Oid, StringComparison.Ordinal);

        /// <summary>
        /// Finds and decodes the paged-results control, or null when the request carries none.
        /// Throws <see cref="LdapProtocolException"/> when the control is present but malformed.
        /// </summary>
        public static LdapPagedResultsRequest? Decode(IReadOnlyList<LdapControl> controls)
        {
            foreach (var control in controls)
            {
                if (!IsSupported(control.Oid))
                    continue;

                // A present control with no value is malformed; there is no defensible default
                // page size to invent on the client's behalf.
                if (control.Value is not { Length: > 0 } value)
                    throw new LdapProtocolException(
                        "the paged results control carries no value; it must be a SEQUENCE of size and cookie.");

                var outer = new BerCursor(value, 0, value.Length);
                var sequence = outer.Child(outer.ReadElement(LdapProtocol.Sequence));
                var size = sequence.Int32(sequence.ReadElement(LdapProtocol.Integer));
                if (size < 0)
                    throw new LdapProtocolException(
                        $"the paged results control requested a negative page size ({size}).");

                var cookie = sequence.Content(sequence.ReadElement(LdapProtocol.OctetString));
                return new LdapPagedResultsRequest(size, cookie);
            }
            return null;
        }

        /// <summary>
        /// Encodes the response control. The size field is 0 in a response (RFC 2696 leaves it
        /// unspecified; servers that report an estimate turn the total result count into an
        /// information channel that survives paging, so this one does not). An empty cookie means
        /// the result set is exhausted.
        /// </summary>
        public static byte[] Encode(byte[] cookie) =>
            BerWriter.Sequence(
                BerWriter.OctetString(Oid),
                BerWriter.Tlv(LdapProtocol.OctetString, BerWriter.Sequence(
                    BerWriter.Integer(0),
                    BerWriter.Tlv(LdapProtocol.OctetString, cookie))));

        /// <summary>
        /// The first CRITICAL control whose OID this server does not implement, or null when every
        /// critical control is supported. Non-critical unsupported controls are ignored, which is
        /// exactly what RFC 4511 §4.1.11 asks for.
        /// </summary>
        public static string? FirstUnsupportedCritical(IReadOnlyList<LdapControl> controls)
        {
            foreach (var control in controls)
            {
                if (control.Criticality && !IsSupported(control.Oid))
                    return control.Oid;
            }
            return null;
        }
    }
}
