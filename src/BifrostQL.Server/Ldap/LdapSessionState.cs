using System.Collections.Generic;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Authentication state of ONE LDAP connection, owned by the connection loop and never shared
    /// between connections. It exists so the loop can answer two questions the wire alone cannot:
    /// whether the peer has authenticated at all (the pre-auth deadline reclaims the admission slot
    /// of one that has not), and whether the authenticated session is the ANONYMOUS one — which may
    /// read only the RootDSE and subschema (criterion 4).
    ///
    /// <para>A failed bind never mutates this: it neither authenticates a session nor downgrades an
    /// already-authenticated one (RFC 4513 §5.1.1). The projected <see cref="UserContext"/> is
    /// carried here for the search slice, which will run every executed operation under it.</para>
    /// </summary>
    internal sealed class LdapSessionState
    {
        /// <summary>Whether a bind on this connection has succeeded (credentialed or admitted-anonymous).</summary>
        public bool Authenticated { get; set; }

        /// <summary>Whether the successful bind was the anonymous one — no identity, discovery reads only.</summary>
        public bool IsAnonymous { get; set; }

        /// <summary>The identity projected by <c>IBifrostAuthContextFactory</c>, or null for an anonymous session.</summary>
        public IDictionary<string, object?>? UserContext { get; set; }
    }
}
