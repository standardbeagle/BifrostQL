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
    internal sealed record LdapBindRequest(int Version, string Name, LdapBindAuthKind AuthKind, byte[]? SimplePassword) : LdapOperation;

    /// <summary>Which authentication choice a BindRequest carried.</summary>
    internal enum LdapBindAuthKind { Simple, Sasl, Unknown }

    /// <summary>SearchRequest: the base object and the parsed filter tree (execution is a non-goal this slice — decoded for cap enforcement).</summary>
    internal sealed record LdapSearchRequest(string BaseObject, int Scope, LdapFilter Filter, IReadOnlyList<string> Attributes) : LdapOperation;

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
        internal sealed record Other(byte Tag) : LdapFilter;
    }

    /// <summary>An attribute of a directory entry in a response: its type and its (string) values.</summary>
    internal sealed record LdapPartialAttribute(string Type, IReadOnlyList<string> Values);

    /// <summary>A SearchResultEntry: an entry DN and its returned attributes (used to encode RootDSE / subschema).</summary>
    internal sealed record LdapSearchResultEntry(string ObjectName, IReadOnlyList<LdapPartialAttribute> Attributes);
}
