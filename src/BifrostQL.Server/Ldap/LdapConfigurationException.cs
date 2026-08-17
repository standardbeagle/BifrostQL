namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Thrown when the LDAP front door is configured in a way it refuses to start with — an LDAPS
    /// listener with no usable certificate, a certificate file that cannot be loaded, a port
    /// collision. It aborts registration/host startup rather than letting the listener come up
    /// degraded, because every degraded state here is a downgrade to cleartext.
    ///
    /// <para>It is raised only from configuration paths and never from a connection, so — unlike a
    /// wire-facing protocol fault — it deliberately derives from <see cref="Exception"/> rather than
    /// from the connection handler's caught <c>LdapProtocolException</c> base
    /// (protocol-adapter-security invariant 1). Mirrors <c>GrpcConfigurationException</c>.</para>
    /// </summary>
    public sealed class LdapConfigurationException : Exception
    {
        public LdapConfigurationException(string message) : base(message) { }

        public LdapConfigurationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
