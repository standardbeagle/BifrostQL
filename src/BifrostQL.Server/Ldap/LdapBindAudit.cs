using System;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// How a bind attempt resolved, recorded for the structured bind audit. Every credentialed
    /// failure maps to the SAME <see cref="LdapResultCode.InvalidCredentials"/> on the wire — this
    /// finer classification is server-side only (audit + lockout policy), never a wire signal, so it
    /// cannot become a user-enumeration oracle.
    /// </summary>
    public enum LdapBindOutcome
    {
        /// <summary>A credentialed simple bind that verified and projected to a real identity.</summary>
        Success,

        /// <summary>An anonymous bind admitted because anonymous binds are explicitly enabled.</summary>
        AnonymousSuccess,

        /// <summary>Unknown DN, wrong password, disabled account, subject-less, or unmapped issuer.</summary>
        InvalidCredentials,

        /// <summary>Refused before any hash work because a per-source or per-account rate limit tripped.</summary>
        RateLimited,

        /// <summary>Refused before any hash work because the presented password exceeded the length cap.</summary>
        PasswordTooLong,

        /// <summary>An anonymous bind refused because anonymous binds are disabled (the default).</summary>
        AnonymousDisabled,
    }

    /// <summary>
    /// One structured bind-audit record. It carries the WHO/WHERE/WHAT-happened of a bind attempt —
    /// the bind DN, the connection source, the outcome, and the time — and deliberately carries
    /// NEITHER the presented password NOR the stored hash, so the audit trail can never become a
    /// credential-disclosure channel (criterion 3).
    /// </summary>
    public sealed record LdapBindAuditRecord(
        LdapBindOutcome Outcome,
        string BindDn,
        string Source,
        DateTimeOffset TimestampUtc);

    /// <summary>
    /// Sink for <see cref="LdapBindAuditRecord"/>s. This single seam serves two roles a deployment
    /// wires together: it is the STRUCTURED BIND AUDIT log, and it is the configurable LOCKOUT HOOK —
    /// a deployment's lockout policy observes the failure stream here and reacts (for example, by
    /// disabling an account in its <see cref="ILdapCredentialStore"/> so the next
    /// <see cref="ILdapCredentialStore.FindAsync"/> returns <see cref="LdapCredentialRecord.Enabled"/>
    /// = false). It is invoked for every attempt on a best-effort basis and must never throw back into
    /// the bind path; it receives no secret material.
    /// </summary>
    public interface ILdapBindObserver
    {
        /// <summary>Records one bind outcome. Called for every attempt; must not throw.</summary>
        void OnBind(LdapBindAuditRecord record);
    }
}
