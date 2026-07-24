namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// LDAPv3 (RFC 4511) wire constants: the BER tags of the message envelope, the
    /// application/context tags of every protocolOp and filter this slice's codec understands,
    /// and the well-known extended-operation / notification OIDs. Collected in one place so the
    /// reader and writer cannot drift on a magic byte.
    ///
    /// <para><b>BER subset.</b> LDAP mandates the DER-ish definite-length BER subset: every
    /// length is definite (indefinite <c>0x80</c> is forbidden), so the codec never needs an
    /// end-of-contents scan and can bound every allocation by a declared length up front.</para>
    /// </summary>
    internal static class LdapProtocol
    {
        // ---- universal BER tags ----
        public const byte Boolean = 0x01;
        public const byte Integer = 0x02;
        public const byte OctetString = 0x04;
        public const byte Enumerated = 0x0A;
        public const byte Sequence = 0x30;   // constructed, universal 16
        public const byte Set = 0x31;        // constructed, universal 17

        // ---- protocolOp application tags (RFC 4511 §4.2-§4.12) ----
        // [APPLICATION n] constructed = 0x60 | n; primitive = 0x40 | n.
        public const byte BindRequest = 0x60;        // [APPLICATION 0]  SEQUENCE
        public const byte BindResponse = 0x61;       // [APPLICATION 1]  SEQUENCE
        public const byte UnbindRequest = 0x42;      // [APPLICATION 2]  NULL (primitive)
        public const byte SearchRequest = 0x63;      // [APPLICATION 3]  SEQUENCE
        public const byte SearchResultEntry = 0x64;  // [APPLICATION 4]  SEQUENCE
        public const byte SearchResultDone = 0x65;   // [APPLICATION 5]  SEQUENCE
        public const byte AbandonRequest = 0x50;     // [APPLICATION 16] INTEGER (primitive)
        public const byte ExtendedRequest = 0x77;    // [APPLICATION 23] SEQUENCE
        public const byte ExtendedResponse = 0x78;   // [APPLICATION 24] SEQUENCE

        // ---- context tags used inside protocolOps ----
        public const byte ControlsTag = 0xA0;              // [0] Controls on the LDAPMessage envelope
        public const byte BindSimpleAuth = 0x80;           // [0] simple authentication (primitive octet string)
        public const byte BindSaslAuth = 0xA3;             // [3] SASL authentication (constructed)
        public const byte ExtendedRequestName = 0x80;      // [0] requestName  (primitive OID string)
        public const byte ExtendedRequestValue = 0x81;     // [1] requestValue (primitive)
        public const byte ExtendedResponseName = 0x8A;     // [10] responseName (primitive OID string)

        // ---- Filter CHOICE tags (RFC 4511 §4.5.1) ----
        public const byte FilterAnd = 0xA0;             // [0] SET OF Filter (constructed)
        public const byte FilterOr = 0xA1;              // [1] SET OF Filter (constructed)
        public const byte FilterNot = 0xA2;             // [2] Filter        (constructed)
        public const byte FilterEqualityMatch = 0xA3;   // [3] AttributeValueAssertion
        public const byte FilterSubstrings = 0xA4;      // [4] SubstringFilter
        public const byte FilterGreaterOrEqual = 0xA5;  // [5] AttributeValueAssertion
        public const byte FilterLessOrEqual = 0xA6;     // [6] AttributeValueAssertion
        public const byte FilterPresent = 0x87;         // [7] AttributeDescription (primitive)
        public const byte FilterApproxMatch = 0xA8;     // [8] AttributeValueAssertion
        public const byte FilterExtensibleMatch = 0xA9; // [9] MatchingRuleAssertion

        // ---- well-known OIDs ----
        /// <summary>StartTLS extended request OID — refused this slice (TLS is a non-goal).</summary>
        public const string StartTlsOid = "1.3.6.1.4.1.1466.20037";

        /// <summary>Notice of Disconnection OID — the unsolicited response sent before a fatal close.</summary>
        public const string NoticeOfDisconnectionOid = "1.3.6.1.4.1.1466.20036";

        /// <summary>The only protocol version this front door speaks.</summary>
        public const int LdapVersion3 = 3;
    }

    /// <summary>
    /// LDAP result codes (RFC 4511 §4.1.9), limited to the set this slice emits. The connection
    /// loop answers every request it understands but does not yet execute with
    /// <see cref="UnwillingToPerform"/>, and a fatal wire violation with <see cref="ProtocolError"/>.
    /// </summary>
    internal enum LdapResultCode
    {
        Success = 0,
        ProtocolError = 2,
        UnavailableCriticalExtension = 12,
        UnwillingToPerform = 53,
    }
}
