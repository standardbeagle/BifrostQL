namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Encodes the LDAP responses this slice emits — BindResponse, SearchResultEntry,
    /// SearchResultDone, ExtendedResponse — wrapped in the LDAPMessage envelope with the request's
    /// message ID. Every response the connection loop can send is produced here, so the wire shape
    /// lives in one place and re-decodes byte-for-byte through <see cref="BerCursor"/>.
    ///
    /// <para>The RootDSE / subschema entries the directory MVP publishes are encoded as ordinary
    /// SearchResultEntry values (see <see cref="LdapDirectoryEntries"/>); the codec can emit them
    /// today even though wiring them to an executed search is a later slice.</para>
    /// </summary>
    internal static class LdapMessageWriter
    {
        /// <summary>Wraps an encoded protocolOp in the LDAPMessage envelope with its message ID.</summary>
        public static byte[] Message(int messageId, byte[] protocolOp)
            => BerWriter.Sequence(BerWriter.Integer(messageId), protocolOp);

        /// <summary>A BindResponse (<c>[APPLICATION 1]</c>) carrying an inlined LDAPResult.</summary>
        public static byte[] BindResponse(int messageId, LdapResultCode code, string diagnosticMessage)
            => Message(messageId, Result(LdapProtocol.BindResponse, code, diagnosticMessage));

        /// <summary>
        /// A SearchResultDone (<c>[APPLICATION 5]</c>) carrying an inlined LDAPResult, plus the
        /// optional response controls (the paged-results cookie) on the LDAPMessage envelope.
        /// </summary>
        public static byte[] SearchResultDone(
            int messageId, LdapResultCode code, string diagnosticMessage, byte[]? responseControls = null)
        {
            var op = Result(LdapProtocol.SearchResultDone, code, diagnosticMessage);
            return responseControls is null
                ? Message(messageId, op)
                : BerWriter.Sequence(
                    BerWriter.Integer(messageId),
                    op,
                    BerWriter.Constructed(LdapProtocol.ControlsTag, responseControls));
        }

        /// <summary>
        /// An ExtendedResponse (<c>[APPLICATION 24]</c>): an inlined LDAPResult plus an optional
        /// responseName OID. Used both as a normal reply and — with message ID 0 — as the
        /// unsolicited Notice of Disconnection sent before a fatal close.
        /// </summary>
        public static byte[] ExtendedResponse(int messageId, LdapResultCode code, string diagnosticMessage, string? responseName = null)
        {
            var content = responseName is null
                ? ResultContent(code, diagnosticMessage)
                : BerWriter.Concat(ResultContent(code, diagnosticMessage), BerWriter.TaggedString(LdapProtocol.ExtendedResponseName, responseName));
            return Message(messageId, BerWriter.Tlv(LdapProtocol.ExtendedResponse, content));
        }

        /// <summary>
        /// The Notice of Disconnection (RFC 4511 §4.4.1): an unsolicited ExtendedResponse on message
        /// ID 0 the server sends immediately before closing on a fatal protocol error, so a client
        /// blocked on a read learns the reason instead of only seeing the socket drop.
        /// </summary>
        public static byte[] NoticeOfDisconnection(LdapResultCode code, string diagnosticMessage)
            => ExtendedResponse(0, code, diagnosticMessage, LdapProtocol.NoticeOfDisconnectionOid);

        /// <summary>A SearchResultEntry (<c>[APPLICATION 4]</c>): the entry DN and its partial-attribute list.</summary>
        public static byte[] SearchResultEntry(int messageId, LdapSearchResultEntry entry)
        {
            var attributes = new byte[entry.Attributes.Count][];
            for (var i = 0; i < entry.Attributes.Count; i++)
            {
                var attribute = entry.Attributes[i];
                var values = new byte[attribute.Values.Count][];
                for (var v = 0; v < attribute.Values.Count; v++)
                    values[v] = BerWriter.OctetString(attribute.Values[v]);
                attributes[i] = BerWriter.Sequence(BerWriter.OctetString(attribute.Type), BerWriter.Set(values));
            }
            var content = BerWriter.Concat(BerWriter.OctetString(entry.ObjectName), BerWriter.Sequence(attributes));
            return Message(messageId, BerWriter.Tlv(LdapProtocol.SearchResultEntry, content));
        }

        // COMPONENTS OF LDAPResult (resultCode, matchedDN, diagnosticMessage) inlined into a response op.
        private static byte[] Result(byte tag, LdapResultCode code, string diagnosticMessage)
            => BerWriter.Tlv(tag, ResultContent(code, diagnosticMessage));

        private static byte[] ResultContent(LdapResultCode code, string diagnosticMessage)
            => BerWriter.Concat(
                BerWriter.Enumerated((long)code),
                BerWriter.OctetString(string.Empty),        // matchedDN
                BerWriter.OctetString(diagnosticMessage));  // diagnosticMessage
    }
}
