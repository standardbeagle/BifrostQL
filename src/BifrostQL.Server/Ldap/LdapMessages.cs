using System.Collections.Generic;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// A decoded LDAP request: the envelope's message ID, the (possibly unrecognized) protocolOp
    /// application tag, and a typed <see cref="Operation"/> for the ops this slice decodes. The
    /// controls envelope is decoded into <see cref="Controls"/> so a critical unsupported control
    /// can be refused per RFC 4511 §4.1.11.
    /// </summary>
    internal sealed record LdapRequest(
        int MessageId,
        byte ProtocolOpTag,
        LdapOperation Operation,
        IReadOnlyList<LdapControl> Controls);

    /// <summary>A request control (<c>[0] Controls</c> on the envelope): its OID, criticality, and opaque value.</summary>
    internal sealed record LdapControl(string Oid, bool Criticality, byte[]? Value);

    /// <summary>The decoded protocolOp of an <see cref="LdapRequest"/>.</summary>
    internal abstract record LdapOperation;

    /// <summary>
    /// BindRequest: the requested protocol version, bind DN, which auth choice was presented, and —
    /// for a Simple bind — the presented password bytes. The password is carried as a mutable
    /// <see cref="byte"/> array (not a string) so the authenticator can zero it after the credential
    /// verify (secret hygiene): an interned/immutable string could not be wiped. It is <c>null</c> for
    /// SASL / unknown / absent auth choices, and a zero-length array for an empty Simple password.
    /// </summary>
    internal sealed record LdapBindRequest(int Version, string Name, LdapBindAuthKind AuthKind, byte[]? SimplePassword) : LdapOperation
    {
        /// <summary>
        /// The RFC 4513 §5.1.1 ANONYMOUS bind: a simple auth choice with an empty name AND an empty
        /// password — a request to be no-one, carrying no credential. Everything else is a
        /// CREDENTIALED bind attempt, including an "unauthenticated bind" (a name with an empty
        /// password) and any SASL/unknown choice.
        ///
        /// <para>Defined once here because two independent decisions turn on it: the transport gate
        /// (an anonymous bind has no secret to protect, so it is exempt from the confidentiality
        /// requirement) and the verifier (an anonymous bind is admitted only when anonymous binds are
        /// explicitly enabled). Two spellings of "anonymous" could drift into a gap where a request
        /// is exempt from the gate but treated as credentialed afterwards.</para>
        /// </summary>
        public bool IsAnonymous =>
            AuthKind == LdapBindAuthKind.Simple
            && string.IsNullOrEmpty(Name)
            && SimplePassword is { Length: 0 };
    }

    /// <summary>Which authentication choice a BindRequest carried.</summary>
    internal enum LdapBindAuthKind { Simple, Sasl, Unknown }

    /// <summary>
    /// SearchRequest (RFC 4511 §4.5.1). Every field the server's behaviour depends on is carried:
    /// the base object and scope, the client's own size/time limits, <c>typesOnly</c>, the parsed
    /// filter tree, and the requested attribute selection.
    ///
    /// <para><see cref="SizeLimit"/> / <see cref="TimeLimit"/> are the CLIENT's requested ceilings;
    /// zero means "no client limit" and the server's own configured cap applies. The effective
    /// limit is always the smaller of the two, so a client can only ever narrow what the server
    /// permits — it can never raise it. <see cref="DerefAliases"/> is decoded for completeness but
    /// alias dereferencing is a declared non-goal, so it never changes resolution.</para>
    /// </summary>
    internal sealed record LdapSearchRequest(
        string BaseObject,
        int Scope,
        int DerefAliases,
        int SizeLimit,
        int TimeLimit,
        bool TypesOnly,
        LdapFilter Filter,
        IReadOnlyList<string> Attributes) : LdapOperation;

    /// <summary>The <c>scope</c> enumeration of a SearchRequest (RFC 4511 §4.5.1.2).</summary>
    internal static class LdapSearchScope
    {
        public const int BaseObject = 0;
        public const int SingleLevel = 1;
        public const int WholeSubtree = 2;
    }

    /// <summary>UnbindRequest: no fields — the client is closing the connection.</summary>
    internal sealed record LdapUnbindRequest : LdapOperation;

    /// <summary>AbandonRequest: the message ID of the operation the client asks to cancel.</summary>
    internal sealed record LdapAbandonRequest(int TargetMessageId) : LdapOperation;

    /// <summary>ExtendedRequest: the request OID (e.g. StartTLS) — refused this slice.</summary>
    internal sealed record LdapExtendedRequest(string RequestName) : LdapOperation;

    /// <summary>An op whose application tag the codec does not decode; the loop answers it with a fatal protocol error.</summary>
    internal sealed record LdapUnknownOperation(byte Tag) : LdapOperation;

    /// <summary>
    /// A decoded LDAP search filter node. Only the recursive connectives (<c>and</c>/<c>or</c>/
    /// <c>not</c>) carry children, so they are the sole nesting vector the depth cap must guard;
    /// leaves (<c>present</c>, equality/relational assertions) and any filter type the slice does
    /// not model in detail (<c>Other</c>) terminate the recursion.
    /// </summary>
    internal abstract record LdapFilter
    {
        internal sealed record And(IReadOnlyList<LdapFilter> Children) : LdapFilter;
        internal sealed record Or(IReadOnlyList<LdapFilter> Children) : LdapFilter;
        internal sealed record Not(LdapFilter Child) : LdapFilter;
        internal sealed record Present(string Attribute) : LdapFilter;
        internal sealed record Comparison(byte Tag, string Attribute, byte[] Value) : LdapFilter;

        /// <summary>
        /// A substring assertion (RFC 4511 §4.5.1.7.2): an optional <c>initial</c> anchor, any
        /// number of unanchored <c>any</c> fragments in order, and an optional <c>final</c> anchor.
        ///
        /// <para>Each fragment is a RAW OCTET STRING, already un-escaped by the client's own RFC
        /// 4515 encoder before it reached the wire. An <c>*</c> byte inside a fragment is therefore
        /// a LITERAL asterisk the client escaped as <c>\2a</c> — never a second wildcard — and the
        /// same holds for every SQL metacharacter. Anything that turns a fragment back into a
        /// pattern must escape it first; see <c>LdapFilterCompiler</c>.</para>
        /// </summary>
        internal sealed record Substrings(
            string Attribute, byte[]? Initial, IReadOnlyList<byte[]> Any, byte[]? Final) : LdapFilter;

        internal sealed record Other(byte Tag) : LdapFilter;
    }

    /// <summary>An attribute of a directory entry in a response: its type and its (string) values.</summary>
    internal sealed record LdapPartialAttribute(string Type, IReadOnlyList<string> Values);

    /// <summary>A SearchResultEntry: an entry DN and its returned attributes (used to encode RootDSE / subschema).</summary>
    internal sealed record LdapSearchResultEntry(string ObjectName, IReadOnlyList<LdapPartialAttribute> Attributes);
}
