namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The result of authenticating a bind, projected to exactly what the connection loop needs to
    /// answer the BindResponse and (on success) run the session. Every failure — unknown DN, wrong
    /// password, disabled account, subject-less principal, unmapped issuer, rate-limited, oversized
    /// password, anonymous-when-disabled — yields the SAME wire shape: result code
    /// <see cref="LdapResultCode.InvalidCredentials"/> with an empty diagnostic. The connection loop
    /// cannot tell the failure classes apart because the result does not distinguish them (criterion
    /// 2, anti-enumeration).
    /// </summary>
    internal readonly struct LdapBindResult
    {
        private LdapBindResult(bool succeeded, bool isAnonymous, IDictionary<string, object?>? userContext, LdapResultCode resultCode)
        {
            Succeeded = succeeded;
            IsAnonymous = isAnonymous;
            UserContext = userContext;
            ResultCode = resultCode;
        }

        /// <summary>Whether the bind authenticated (credentialed or admitted-anonymous).</summary>
        public bool Succeeded { get; }

        /// <summary>Whether the bind is an admitted anonymous bind (reads only RootDSE/subschema — criterion 4).</summary>
        public bool IsAnonymous { get; }

        /// <summary>The projected user context on a credentialed success, else null.</summary>
        public IDictionary<string, object?>? UserContext { get; }

        /// <summary>The result code to answer with: Success or (for every failure) InvalidCredentials.</summary>
        public LdapResultCode ResultCode { get; }

        /// <summary>The uniform, information-free diagnostic every bind response carries.</summary>
        public string DiagnosticMessage => string.Empty;

        public static LdapBindResult Success(IDictionary<string, object?> userContext) =>
            new(succeeded: true, isAnonymous: false, userContext, LdapResultCode.Success);

        /// <summary>An admitted anonymous bind — success with no identity, limited to RootDSE/subschema.</summary>
        public static LdapBindResult Anonymous() =>
            new(succeeded: true, isAnonymous: true, userContext: null, LdapResultCode.Success);

        /// <summary>The single failure shape shared by every failure class (anti-enumeration).</summary>
        public static LdapBindResult Invalid() =>
            new(succeeded: false, isAnonymous: false, userContext: null, LdapResultCode.InvalidCredentials);
    }
}
