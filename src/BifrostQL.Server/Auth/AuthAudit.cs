using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Auth
{
    /// <summary>What an authentication audit event records.</summary>
    public enum AuthAuditKind
    {
        LoginSucceeded,
        LoginFailed,
        Logout,
        AccountLocked,
        AccountUnlocked,
        PasswordChanged,
        PasswordChangeRejected,
        SessionRevoked,
        PrivilegeChanged,
    }

    /// <summary>Whether the audited action took effect.</summary>
    public enum AuthAuditOutcome
    {
        Success,
        Failure,
    }

    /// <summary>
    /// One authentication audit event: when, what, whose account, who acted, from where, and how
    /// it ended. It never carries a password, a password hash, a session id, a cookie or a token,
    /// and <see cref="Reason"/> comes from a fixed vocabulary (<see cref="AuthAuditReasons"/>,
    /// <see cref="LocalSessionRevokeReasons"/>), never from request input.
    /// </summary>
    /// <param name="EventId">Unique id of the event.</param>
    /// <param name="OccurredAt">When it happened (UTC).</param>
    /// <param name="Kind">What happened.</param>
    /// <param name="Outcome">Whether it took effect.</param>
    /// <param name="SubjectId">The app-user id whose account the event concerns, or null when no account matched.</param>
    /// <param name="ActorId">The app-user id that caused it when that is not the subject (an administrator), else null.</param>
    /// <param name="SourceIp">The client IP of the request, or null when there was none.</param>
    /// <param name="Reason">A fixed-vocabulary reason, or null.</param>
    public sealed record AuthAuditEvent(
        Guid EventId,
        DateTimeOffset OccurredAt,
        AuthAuditKind Kind,
        AuthAuditOutcome Outcome,
        string? SubjectId,
        string? ActorId,
        string? SourceIp,
        string? Reason);

    /// <summary>The fixed reason vocabulary of authentication audit events (beside <see cref="LocalSessionRevokeReasons"/>).</summary>
    public static class AuthAuditReasons
    {
        public const string BadCredentials = "bad-credentials";
        public const string UnknownAccount = "unknown-account";
        public const string AccountLocked = "account-locked";
        public const string AccountDisabled = "account-disabled";
        public const string PasswordExpired = "password-expired";
        public const string PasswordPolicy = "password-policy";
        public const string LockoutExpired = "lockout-expired";
        public const string Administrator = "administrator";
        public const string Roles = "roles";
        public const string Tenant = "tenant";
    }

    /// <summary>
    /// An extra destination for authentication audit events (a SIEM forwarder, say). Every event is
    /// first written to <see cref="ILocalAuthStateStore.AppendAuditAsync"/> and the
    /// <c>BifrostQL.Audit.Authentication</c> log category; registered sinks receive it after. A
    /// throwing sink fails the request that raised the event.
    /// </summary>
    public interface IAuthAuditSink
    {
        Task WriteAsync(AuthAuditEvent auditEvent, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The one place local auth raises an audit event: the state store's audit table, the
    /// <c>BifrostQL.Audit.Authentication</c> log category, then every registered
    /// <see cref="IAuthAuditSink"/>, in that order. Write failures propagate.
    /// </summary>
    internal sealed class LocalAuthAudit
    {
        public const string LogCategory = "BifrostQL.Audit.Authentication";

        private readonly ILocalAuthStateStore _store;
        private readonly IReadOnlyList<IAuthAuditSink> _sinks;
        private readonly ILogger _logger;

        public LocalAuthAudit(ILocalAuthStateStore store, IEnumerable<IAuthAuditSink> sinks, ILoggerFactory? loggerFactory)
        {
            _store = store;
            _sinks = sinks.ToList();
            _logger = (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger(LogCategory);
        }

        public async Task RecordAsync(
            DateTimeOffset at, AuthAuditKind kind, AuthAuditOutcome outcome,
            string? subjectId, string? actorId, string? sourceIp, string? reason, CancellationToken cancellationToken)
        {
            var auditEvent = new AuthAuditEvent(Guid.NewGuid(), at, kind, outcome, subjectId, actorId, sourceIp, reason);
            await _store.AppendAuditAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Auth audit {AuditKind} {AuditOutcome} subject={SubjectId} actor={ActorId} source={SourceIp} reason={Reason} event={EventId}",
                auditEvent.Kind, auditEvent.Outcome, auditEvent.SubjectId, auditEvent.ActorId, auditEvent.SourceIp,
                auditEvent.Reason, auditEvent.EventId);
            foreach (var sink in _sinks)
                await sink.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
