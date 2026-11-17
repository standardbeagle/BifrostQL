---
title: "Compliance Controls: HIPAA, SOX and PCI DSS"
description: "Which HIPAA, SOX and PCI DSS requirements BifrostQL's shipped controls meet, the option that sets each one, and the gaps the deployment has to close itself, each with its owner."
---

This page maps the access, authentication and audit requirements of HIPAA (45 CFR
164.312), SOX IT general controls and PCI DSS v4.0 onto what BifrostQL ships. Each
**supported** row names the option that controls it and the code that enforces it,
with the number of production call sites that reach the guard. Each **gap** row names
who has to close it.

Nothing here makes a deployment compliant on its own. An assessor looks at the
whole system: the database, the host, the network and the processes around them.
BifrostQL covers the parts that live in the API layer.

## Local-auth sessions and accounts

These controls apply to the local DB-backed login (`AddBifrostLocalAuth`). Sessions
signed in through OIDC are owned by the identity provider; see [Gaps](#gaps).

| Requirement | Control | Option (default) | Enforced by |
|---|---|---|---|
| PCI 8.2.8, HIPAA 164.312(a)(2)(iii) automatic logoff | A session idle for the timeout is revoked server-side. | `SessionIdleTimeout` (15 min) | `LocalSessionManager.CheckAsync`, called from `LocalAuthEndpoint.ValidateSessionAsync` (1 call site), the cookie `OnValidatePrincipal` hook, so every authenticated request runs it. |
| Absolute session limit | A session ends a fixed time after sign-in, active or not. | `SessionLifetime` (8 h) | The `ExpiresAt` check in `CheckAsync`, plus the cookie expiry. |
| PCI 8.2.6 / 8.2.2 terminate access | Logout revokes the server session, not just the browser cookie. Password change, disable, delete and a role, tenant or household change revoke every session of the user. An administrator can revoke all of a user's sessions. | `SessionRevalidationInterval` (1 min), `DisabledColumn`, `AdminRole` | `LocalSessionManager.RevokeAllAsync` (3 call sites: revalidation, password change, administrator), `LocalAuthAdministration.RevokeAllSessionsAsync`. |
| Concurrent sessions | A sign-in past the cap revokes the user's oldest sessions. | `MaxConcurrentSessions` (no cap) | `LocalSessionManager.IssueAsync`. |
| PCI 8.3.4 lockout | 10 consecutive failures lock the account for 30 minutes, or until an administrator unlocks it. A locked account answers exactly like a wrong password. Separate from the per-IP and per-account rate windows. | `LockoutThreshold` (10), `LockoutDuration` (30 min; null = until unlocked) | `RefuseLockedAsync` / `RecordFailedAttemptAsync` in `LocalAuthEndpoint` (2 call sites each: login and password change). |
| Online-guessing throttle | Attempts are counted per source IP and per account before the hash runs. | `MaxLoginAttemptsPerSource` (20), `MaxLoginAttemptsPerAccount` (10), `LoginAttemptWindow` (5 min) | `LocalLoginRateLimiter.TryLogin` (2 call sites: login and password change). |
| PCI 8.3.6 password strength | At least 12 characters, with a letter and a digit. | `MinPasswordLength` (12), `PasswordRequiresLetterAndDigit` (true), `MaxPasswordLength` (256) | `PasswordPolicyViolationAsync` on the password-change endpoint. |
| PCI 8.3.7 password reuse | Not the current password or any of the last 4. | `PasswordHistoryCount` (4) | Same; hashes kept in `_bifrost_auth_password_history`. |
| PCI 8.3.9 rotation | A password older than the limit cannot sign in until it is changed. | `MaxPasswordAge` (off) | `PasswordExpiredAsync` in the login handler. |
| PCI 8.3.2 credential storage | PBKDF2-HMAC-SHA512, 210,000 iterations (the OWASP Password Storage Cheat Sheet figure). Older hashes are upgraded at the next sign-in. Below 210,000 fails at startup. | `PasswordHashIterations` (210,000) | `LocalAuthOptions.CreatePasswordHasher`. |
| PCI 8.2.1, HIPAA 164.312(a)(2)(i) unique user id | Every session carries the app-user's stable id. | `IdColumn` | `AppIdentity.Id`. |

Session ids are 256-bit random values. The cookie carries the id; the database keeps
only its SHA-256. The state tables (`_bifrost_auth_sessions`, `_bifrost_auth_lockouts`,
`_bifrost_auth_password_history`, `_bifrost_auth_audit`) live beside the app-user table.
They are created on first use and are never part of a served schema, whatever their
metadata says (`BifrostSystemTables.IsAlwaysHidden`).

## Audit trail

| Requirement | Control | Where it goes |
|---|---|---|
| PCI 10.2.1.4 invalid access attempts, 10.2.1.5 credential changes, 10.2.1.2 administrator actions; HIPAA 164.312(b) audit controls; SOX access logging | Login success and failure, logout, account locked, unlocked, password changed or rejected, session revoked, privilege changed. | `_bifrost_auth_audit`, the `BifrostQL.Audit.Authentication` log category, and every registered `IAuthAuditSink`. |
| PCI 10.2.2 record content | Each event records time (UTC), kind, outcome, subject user id, acting administrator, source IP and a fixed-vocabulary reason. It never records a password, hash, session id, cookie or the login name typed. | `AuthAuditEvent` |
| HIPAA 164.312(c)(1) integrity, SOX change trail | Who changed which column from what to what, in the same transaction as the write. | [Change history](/BifrostQL/guides/change-history/) |
| Record attribution | Created-by and updated-by columns filled from the caller's identity. | `AuditMutationTransformer` |

An audit write that fails fails the request: an unrecorded event is an error, not a
warning.

## Data protection

| Requirement | Control | Docs |
|---|---|---|
| HIPAA 164.312(a)(2)(iv), PCI 3.5 encryption of stored data | Column-level encryption with masking for roles that may not read plaintext. | [Field encryption](/BifrostQL/concepts/field-encryption/) |
| PCI 3.6 / 3.7 key management | Data-encryption-key rotation with an online re-encryption sweep. | [Rotating keys](/BifrostQL/guides/field-encryption/) |
| HIPAA 164.312(a)(1), PCI 7 least privilege | Row and column policies; tenant isolation. | [Authorization](/BifrostQL/guides/authorization/) |
| Retention and erasure | Scheduled purge policies; tombstoning of the history trail. | [Retention](/BifrostQL/guides/retention/) |
| SOX segregation of duties | A write can require a second person's approval. | [Approval workflows](/BifrostQL/guides/approval-workflows/) |

## Gaps

| Gap | Requirement | Owner | Notes |
|---|---|---|---|
| Multi-factor authentication | PCI 8.4.2 (all access to the cardholder data environment), HIPAA 164.312(d) | Host, through an OIDC provider that enforces MFA | Local auth has no second factor. Design notes for TOTP are below. |
| OIDC session controls | PCI 8.2.8, 8.3.4 for OIDC users | Identity provider | Idle timeout, lockout, revocation and audit above cover local auth only. An OIDC cookie is checked against the provider's own session rules. |
| Read-access audit | PCI 10.2.1.1 (access to cardholder data) | Host | No shipped observer records reads. `IQueryObserver` can, but observer exceptions are logged and do not abort the query, so it cannot fail closed. |
| Inactive-account disable | PCI 8.2.6 (90 days) | Host | `DisabledColumn` honours a flag; nothing sets it on inactivity. A scheduled job over `_bifrost_auth_audit` login events can. |
| Encryption at rest (whole database) | HIPAA 164.312(a)(2)(iv), PCI 3.5 | Database operator | TDE or volume encryption. Field encryption covers chosen columns only. |
| Root-key rotation | PCI 3.7.4 | Host | No shipped re-wrap API; see [Rotating keys](/BifrostQL/guides/field-encryption/). The ASP.NET Core Data Protection keys that sign session cookies are the host's to persist and rotate. |
| Transport encryption | HIPAA 164.312(e)(1), PCI 4.2 | Host | Terminate TLS in Kestrel or a proxy, and configure TLS on every wire adapter you enable. gRPC serves cleartext h2c unless `RequireTls` is set, with a startup warning. |
| Audit and log retention, tamper evidence | PCI 10.5.1 (12 months), 10.3 | Operator | `_bifrost_auth_audit` is append-only from BifrostQL but has no purge or hash chain. Ship the log category to a SIEM with write-once retention. |
| Network segmentation | PCI 1.x | Operator | Every non-HTTP listener binds loopback by default; widening is the operator's decision (see the posture table in the protocol-adapter guide). |

### MFA design notes

A TOTP second factor fits the session design without changing the cookie contract:

1. Store a per-user TOTP secret encrypted with field encryption, never in plaintext.
2. After the password verifies, issue a short-lived "password-verified" session row
   with no privileges instead of the full session, and require
   `POST /auth/mfa {code}` to promote it.
3. Count wrong codes toward the same lockout counter, and audit `MfaFailed` /
   `MfaSucceeded`.
4. Reject a code reused within its time step. Accept ±1 step of clock skew.
5. Provide recovery codes, stored hashed, and an administrator reset that is audited.

It is not shipped because step 2 changes what a successful `/auth/login` means for
every existing client, and the secret store depends on a field-encryption key-ref
each deployment has to choose.
